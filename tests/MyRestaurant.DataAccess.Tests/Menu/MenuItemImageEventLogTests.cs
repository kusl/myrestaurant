using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Menu;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuItemImageEventLog"/> against a real PostgreSQL 17 container —
/// §11.4's per-item picture history, which is what <c>ManageMenuItem.razor</c> renders under its picture
/// panel and what nothing in this tree could read before Stage 4d of
/// <c>docs/MENU_AND_HANDHELD_PLAN.md</c>.
///
/// <para><b>Three properties carry the weight, and they are the same three <c>MenuEventLogTests</c> and
/// <c>MenuSectionEventLogTests</c> each pin one table over</b> — deliberately, because a third reader
/// written to a different standard is how three histories of one menu start disagreeing.</para>
///
/// <para><b>The stream is complete.</b> §11.4 requires administration to render "the complete stored
/// record everywhere — full event streams … never projected or truncated for the administrator", so the
/// read has no cap and no filter, and <see cref="ListForItem_ReturnsEveryEventOldestFirst"/> asserts that
/// against a stream written by all three write verbs.</para>
///
/// <para><b>The payloads arrive as stored.</b> §8.2's three named paired CHECKs make each event type carry
/// a different shape, and a reader that mixed them up would render a history that reads plausibly and says
/// something false — the worse of the two failures in an append-only system (ADR-0002).
/// <see cref="ListForItem_CarriesExactlyThePayloadEachEventTypeIsAllowed"/> pins all four types, including
/// the one whose entire payload is its own name.</para>
///
/// <para><b>And one property no other reader in this layer has to have: the history outlives its
/// subject.</b> A replace deletes the row it replaces and a removal deletes the row outright (§7's stated
/// exception to §6.8), so an event on this table names a <c>menu_item_image_identifier</c> that mostly no
/// longer exists. <see cref="ListForItem_KeepsTheWholeHistoryOfPicturesThatAreGone"/> is the fact that
/// would fail on the reader a maintainer would write by reflex, because every other reader in this family
/// joins the row its events are about and <em>this one must not</em>: an INNER JOIN to
/// <c>menu_item_image</c> would return a history that silently begins at the current photograph, and a LEFT
/// JOIN would add a column that is null on every row but the newest. That is also the whole reason
/// <c>0006</c> declared no foreign key there, and it is asserted here rather than trusted to the comment
/// that says so.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class MenuItemImageEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    /// <summary>
    /// A real PNG signature followed by padding. The write reads the first eight bytes and no more (§7),
    /// so padding is the honest way to have a picture at all without committing a binary to this
    /// repository — the same arrangement <c>MenuItemImageTests</c> uses, and the byte length below is what
    /// the <c>attached</c> and <c>replaced</c> payloads are asserted against.
    /// </summary>
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    /// <summary>
    /// A real JPEG, so that a replacement can differ from what it replaces in the one payload column that
    /// would otherwise be indistinguishable. Two PNGs would make
    /// <see cref="ListForItem_CarriesExactlyThePayloadEachEventTypeIsAllowed"/> pass against a reader that
    /// read the <em>attach's</em> content type onto the replace's row.
    /// </summary>
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 19, 10, 15, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    /// <summary>
    /// A second actor with no display name, so the reader's fallback to the username is exercised rather
    /// than assumed. Every other reader in this layer carries the same <c>COALESCE(NULLIF(btrim(…)))</c>
    /// and a new one is exactly where it would be forgotten.
    ///
    /// <para><b>The username is three characters and that is not a style choice (F-85).</b>
    /// <c>person.username</c> has carried <c>CHECK (char_length(username) BETWEEN 3 AND 64)</c> since
    /// <c>0001</c>, and the class that first arranged a nameless actor asked for <c>"mo"</c> — so every one
    /// of its six facts failed in <c>InitializeAsync</c>, before a single assertion ran, with a constraint
    /// name rather than a sentence. The rule is written here beside the field, which is where the copies
    /// that got it right keep it.</para>
    /// </summary>
    private Guid _managerIdentifier;

    public MenuItemImageEventLogTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is null)
        {
            return;
        }

        new SchemaMigrationRunner(_fixture.ConnectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        }.Run();

        _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);
        _world = new OrderTestWorld(_connectionFactory, _clock, _identifiers);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // TRUNCATE … CASCADE on menu_item reaches both of 0006's tables, since both reference it.
        await _world.TruncateAsync(cancellationToken);

        _administratorIdentifier = await _world.AddPersonAsync("adam", "Adam Osei", cancellationToken);
        _managerIdentifier = await _world.AddPersonAsync("moe", null, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    /// <summary>
    /// Every event, oldest first, uncapped — §11.4's rule, asserted against a stream written by all three
    /// picture verbs. A history reads forward, so the first photograph is at the top and the removal is at
    /// the bottom.
    /// </summary>
    [Fact]
    public async Task ListForItem_ReturnsEveryEventOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        await AttachAsync(salmon, ImageFormat.PngContentType, PngBytes, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().SetMenuItemImageAltTextAsync(
            salmon, "On a bed of wilted greens", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await AttachAsync(salmon, ImageFormat.JpegContentType, JpegBytes, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RemoveMenuItemImageAsync(
            salmon, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuItemImageEventEntry> history =
            await Log().ListForItemAsync(salmon, cancellationToken);

        Assert.Equal(
            new[] { "attached", "alt_text_changed", "replaced", "removed" },
            history.Select(entry => entry.EventType).ToArray());

        Assert.Equal(history.OrderBy(entry => entry.OccurredAt).ToArray(), history.ToArray());
        Assert.All(history, entry => Assert.Equal(salmon, entry.MenuItemIdentifier));

        // The item's name is a read-time join, so every entry reads under the name the dish has NOW. There
        // is no per-event name payload on this table to disagree with it — a picture event records nothing
        // about the dish — which is why one assertion covers the whole stream here where the section log
        // needs the distinction spelled out.
        Assert.All(history, entry => Assert.Equal("Salmon", entry.MenuItemName));
    }

    /// <summary>
    /// §8.2's three named biconditionals, carried through the reader unchanged. <c>removed</c> is the
    /// interesting one: its entire payload is its own name, so all three columns must come back null and a
    /// reader that defaulted one of them would make the history say something nobody wrote. The caption
    /// column is the mirror-image case — non-null on <c>alt_text_changed</c> and on nothing else, which is
    /// why <c>0007</c> had to widen neither existing biconditional.
    /// </summary>
    [Fact]
    public async Task ListForItem_CarriesExactlyThePayloadEachEventTypeIsAllowed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        await AttachAsync(salmon, ImageFormat.PngContentType, PngBytes, cancellationToken);
        await Administration().SetMenuItemImageAltTextAsync(
            salmon, "On a bed of wilted greens", _administratorIdentifier, cancellationToken);
        await AttachAsync(salmon, ImageFormat.JpegContentType, JpegBytes, cancellationToken);
        await Administration().RemoveMenuItemImageAsync(
            salmon, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuItemImageEventEntry> history =
            await Log().ListForItemAsync(salmon, cancellationToken);

        // LINQ's Single, not Assert.Single's predicate overload: it throws just as loudly when the count is
        // wrong, and it is the same call on every xUnit line this file might be read on.
        MenuItemImageEventEntry Entry(string eventType) => history.Single(entry => entry.EventType == eventType);

        MenuItemImageEventEntry attached = Entry("attached");
        Assert.Equal(ImageFormat.PngContentType, attached.NewContentType);
        Assert.Equal(PngBytes.Length, attached.NewByteLength);
        Assert.Null(attached.NewAltText);

        // The replace's payload is its OWN picture's, not the one it replaced. Both halves are asserted
        // because a reader that carried the wrong row's content type forward would still return a JPEG's
        // length beside a PNG's type and read plausibly.
        MenuItemImageEventEntry replaced = Entry("replaced");
        Assert.Equal(ImageFormat.JpegContentType, replaced.NewContentType);
        Assert.Equal(JpegBytes.Length, replaced.NewByteLength);
        Assert.Null(replaced.NewAltText);

        MenuItemImageEventEntry captioned = Entry("alt_text_changed");
        Assert.Null(captioned.NewContentType);
        Assert.Null(captioned.NewByteLength);
        Assert.Equal("On a bed of wilted greens", captioned.NewAltText);

        MenuItemImageEventEntry removed = Entry("removed");
        Assert.Null(removed.NewContentType);
        Assert.Null(removed.NewByteLength);
        Assert.Null(removed.NewAltText);
    }

    /// <summary>
    /// The whole history survives every picture it is about, which is the property this reader has and the
    /// other two in this family do not. A replace mints a new identifier and deletes the old row so that
    /// §7's <c>Cache-Control: immutable</c> is a true statement; a removal deletes the row outright. So
    /// after this arrangement <b>three of the four events name a picture that no longer exists and the
    /// fourth names one that never will</b> — and the reader must still return all four.
    ///
    /// <para><b>This is the fact that fails on the reader written by reflex.</b> Every other reader in this
    /// layer joins the row its events are about; an INNER JOIN to <c>menu_item_image</c> here returns a
    /// history beginning at the current photograph, which reads like a complete history and is not one.
    /// <c>0006</c> declared no foreign key on that column precisely so that this can be true, and the
    /// identifiers are asserted individually because "all four rows came back" would also pass on a reader
    /// that returned four rows carrying the same identifier.</para>
    /// </summary>
    [Fact]
    public async Task ListForItem_KeepsTheWholeHistoryOfPicturesThatAreGone()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        Guid first = await AttachAsync(salmon, ImageFormat.PngContentType, PngBytes, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        Guid second = await AttachAsync(salmon, ImageFormat.JpegContentType, JpegBytes, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RemoveMenuItemImageAsync(
            salmon, _administratorIdentifier, cancellationToken);

        // Nothing is attached now, which is the state §11.4 could not describe before this reader existed.
        Assert.Null(await Directory().FindForItemAsync(salmon, cancellationToken));

        IReadOnlyList<MenuItemImageEventEntry> history =
            await Log().ListForItemAsync(salmon, cancellationToken);

        Assert.Equal(3, history.Count);
        Assert.Equal(
            new[] { first, second, second },
            history.Select(entry => entry.MenuItemImageIdentifier).ToArray());

        // A replace mints a NEW identifier, which is what makes the route's immutable cache header honest
        // — asserted here as well as on the write side because a history that repeated one identifier
        // would tell an administrator the address never changed.
        Assert.NotEqual(first, second);

        // And neither picture can be fetched any more, so the identifiers above are evidence rather than
        // links. §7's route answers 404 for both, which is the reason the surface renders no URL.
        Assert.Null(await Directory().ReadContentAsync(first, cancellationToken));
        Assert.Null(await Directory().ReadContentAsync(second, cancellationToken));
    }

    /// <summary>
    /// Clearing a caption is an ordinary change with an ordinary event carrying <c>""</c> — not a deletion
    /// and not a null. That is the entire reason <c>menu_item_image.alt_text</c> is
    /// <c>NOT NULL DEFAULT ''</c>: §8.2's paired CHECK could not tie an optional payload to its event type
    /// if clearing wrote NULL, and the surface distinguishing "cleared" from "set" reads the length rather
    /// than testing for null.
    /// </summary>
    [Fact]
    public async Task ListForItem_AClearedCaptionIsAnEventCarryingTheEmptyString()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        await AttachAsync(salmon, ImageFormat.PngContentType, PngBytes, cancellationToken);

        await Administration().SetMenuItemImageAltTextAsync(
            salmon, "On a bed of wilted greens", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().SetMenuItemImageAltTextAsync(
            salmon, string.Empty, _administratorIdentifier, cancellationToken);

        MenuItemImageEventEntry[] captions = (await Log().ListForItemAsync(salmon, cancellationToken))
            .Where(entry => entry.EventType == "alt_text_changed")
            .ToArray();

        // Compared against string.Empty rather than asserted NotNull-then-Empty: the distinction under test
        // is "" versus null, and one equality states it without depending on how an assertion library
        // annotates its parameters for flow analysis.
        Assert.Equal(2, captions.Length);
        Assert.Equal("On a bed of wilted greens", captions[0].NewAltText);
        Assert.Equal(string.Empty, captions[1].NewAltText);
    }

    /// <summary>
    /// Who did it, rendered the way every other reader in this layer renders a person: the display name,
    /// falling back to the username when there is none rather than to blank.
    /// </summary>
    [Fact]
    public async Task ListForItem_NamesTheActorAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        await AttachAsync(salmon, ImageFormat.PngContentType, PngBytes, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().RemoveMenuItemImageAsync(salmon, _managerIdentifier, cancellationToken);

        IReadOnlyList<MenuItemImageEventEntry> history =
            await Log().ListForItemAsync(salmon, cancellationToken);

        MenuItemImageEventEntry attached = history.Single(entry => entry.EventType == "attached");
        Assert.Equal(_administratorIdentifier, attached.ActorPersonIdentifier);
        Assert.Equal("Adam Osei", attached.ActorName);

        MenuItemImageEventEntry removed = history.Single(entry => entry.EventType == "removed");
        Assert.Equal(_managerIdentifier, removed.ActorPersonIdentifier);
        Assert.Equal("moe", removed.ActorName);
    }

    /// <summary>
    /// One dish's picture history is one dish's picture history. A reader that dropped the WHERE — or
    /// joined <c>menu_item</c> without qualifying <c>menu_item_identifier</c>, which is PostgreSQL error
    /// 42702 on a good day and a wrong answer on a bad one — would return the whole table here, and the
    /// page above renders whatever it is handed.
    ///
    /// <para>The empty cases are two rather than one, and both are ordinary: an item that has never had a
    /// picture, and an identifier that names nothing at all. §11.4's panel says so in a sentence rather
    /// than reporting an error, which is why neither may throw.</para>
    /// </summary>
    [Fact]
    public async Task ListForItem_ReturnsNothingFromAnotherItemAndNothingForAnItemWithNoPicture()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid tart = await World().AddMenuItemAsync("Tart", 6.00m, cancellationToken);
        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        await AttachAsync(salmon, ImageFormat.PngContentType, PngBytes, cancellationToken);

        await AttachAsync(tart, ImageFormat.PngContentType, PngBytes, cancellationToken);
        await Administration().RemoveMenuItemImageAsync(tart, _administratorIdentifier, cancellationToken);

        Assert.Single(await Log().ListForItemAsync(salmon, cancellationToken));
        Assert.Equal(2, (await Log().ListForItemAsync(tart, cancellationToken)).Count);

        // An item on the menu that has never been photographed, and an identifier that names no item at
        // all. Both are an empty list rather than an error.
        Assert.Empty(await Log().ListForItemAsync(soup, cancellationToken));
        Assert.Empty(await Log().ListForItemAsync(_identifiers.Create(), cancellationToken));
    }

    /// <summary>
    /// One picture, attached through the write service so that its event is the real one, returning the
    /// identifier the service says it stored rather than the one this method minted — on
    /// <see cref="AttachMenuItemImageResult"/>'s own reasoning: a caller that generated an identifier and
    /// had it refused must not go on to use it.
    /// </summary>
    private async Task<Guid> AttachAsync(
        Guid menuItemIdentifier,
        string contentType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        AttachMenuItemImageResult result = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            menuItemIdentifier,
            contentType,
            bytes,
            _administratorIdentifier,
            cancellationToken);

        Assert.NotNull(result.MenuItemImageIdentifier);
        return result.MenuItemImageIdentifier!.Value;
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(
            _fixture.ConnectionString is not null,
            _fixture.SkipReason ?? "No container engine.");

    private DapperMenuItemImageEventLog Log() => new(_connectionFactory!);

    private DapperMenuItemImageAdministration Administration()
        => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuItemImageDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
