using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuSectionEventLog"/> against a real PostgreSQL 17 container —
/// §11.4's per-heading event history, which is what the section editor renders at the bottom of its page
/// and what nothing in this tree could read before it.
///
/// <para>Three properties carry the weight, and they are the same three
/// <c>MenuEventLogTests</c> pins one table over — deliberately, because a second reader written to a
/// different standard is how two histories of one menu start disagreeing.</para>
///
/// <para><b>The stream is complete.</b> §11.4 requires administration to render "the complete stored
/// record everywhere — full event streams … never projected or truncated for the administrator", so the
/// read has no cap and no filter, and <see cref="ListForSection_ReturnsEveryEventOldestFirst"/> asserts
/// that against a stream written by all five verbs.</para>
///
/// <para><b>The payloads arrive as stored.</b> §8.2's three named paired CHECKs make each event type carry
/// a different shape, and a reader that mixed them up would render a history that reads plausibly and says
/// something false — the worse of the two failures in an append-only system (ADR-0002).
/// <see cref="ListForSection_CarriesExactlyThePayloadEachEventTypeIsAllowed"/> pins all six types,
/// including the two whose entire payload is their own name.</para>
///
/// <para><b>The heading's own <c>created</c> carries all three payloads</b>, which is the opposite of
/// <c>menu_item_event</c>'s rule and is the fact most likely to be got backwards by somebody holding both
/// files open. An item's <c>created</c> is kept at the name and the price, so creating one under a heading
/// writes three rows; a section's carries the name, the description <em>and</em> the position, so creating
/// a heading writes exactly one.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class MenuSectionEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 16, 9, 30, 0, TimeSpan.Zero));
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
    /// <c>person.username</c> carries <c>CHECK (char_length(username) BETWEEN 3 AND 64)</c> from
    /// <c>0001</c>, and this class arrived asking for <c>"mo"</c> — so every one of its six facts failed
    /// in <c>InitializeAsync</c>, before a single assertion ran, with a constraint name rather than a
    /// sentence. <c>EventExplorerReadsTests</c> carries a comment stating the minimum directly above its
    /// own four people; this file is the copy that did not.</para>
    /// </summary>
    private Guid _managerIdentifier;

    public MenuSectionEventLogTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    /// Every event, oldest first, uncapped — §11.4's rule, asserted against a stream written by all five
    /// verbs. A history reads forward, so <c>created</c> is at the top and the most recent change is at
    /// the bottom.
    /// </summary>
    [Fact]
    public async Task ListForSection_ReturnsEveryEventOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RenameMenuSectionAsync(
            drinks, "Drinks & cordials", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().DescribeMenuSectionAsync(
            drinks, "Served all day", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().ReorderMenuSectionAsync(
            drinks, 3, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: false, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: true, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuSectionEventEntry> history =
            await Log().ListForSectionAsync(drinks, cancellationToken);

        Assert.Equal(
            new[] { "created", "renamed", "described", "reordered", "deactivated", "activated" },
            history.Select(entry => entry.EventType).ToArray());

        Assert.Equal(history.OrderBy(entry => entry.OccurredAt).ToArray(), history.ToArray());
        Assert.All(history, entry => Assert.Equal(drinks, entry.MenuSectionIdentifier));

        // The section's name is a read-time join, so every entry reads under the name it has NOW while
        // each rename's payload still says what it was set to then. That is the distinction that lets
        // somebody follow a rename rather than be confused by it.
        Assert.All(history, entry => Assert.Equal("Drinks & cordials", entry.MenuSectionName));
    }

    /// <summary>
    /// §8.2's three named biconditionals, carried through the reader unchanged. The two activation types
    /// are the interesting ones: their entire payload is their own name, so all three columns must come
    /// back null and a reader that defaulted one of them to a value would make the history say something
    /// nobody wrote.
    /// </summary>
    [Fact]
    public async Task ListForSection_CarriesExactlyThePayloadEachEventTypeIsAllowed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        await Administration().RenameMenuSectionAsync(
            drinks, "Cordials", _administratorIdentifier, cancellationToken);
        await Administration().DescribeMenuSectionAsync(
            drinks, "Served all day", _administratorIdentifier, cancellationToken);
        await Administration().ReorderMenuSectionAsync(
            drinks, 2, _administratorIdentifier, cancellationToken);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: false, _administratorIdentifier, cancellationToken);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: true, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuSectionEventEntry> history =
            await Log().ListForSectionAsync(drinks, cancellationToken);

        // LINQ's Single, not Assert.Single's predicate overload: it throws just as loudly when the count
        // is wrong, and it is the same call on every xUnit line this file might be read on.
        MenuSectionEventEntry Entry(string eventType) => history.Single(entry => entry.EventType == eventType);

        // 'created' carries ALL THREE, which is the opposite of menu_item_event's rule.
        MenuSectionEventEntry created = Entry("created");
        Assert.Equal("Drinks", created.NewName);
        Assert.Equal("Cold things", created.NewDescription);
        Assert.Equal(0, created.NewDisplayOrder);

        MenuSectionEventEntry renamed = Entry("renamed");
        Assert.Equal("Cordials", renamed.NewName);
        Assert.Null(renamed.NewDescription);
        Assert.Null(renamed.NewDisplayOrder);

        MenuSectionEventEntry described = Entry("described");
        Assert.Null(described.NewName);
        Assert.Equal("Served all day", described.NewDescription);
        Assert.Null(described.NewDisplayOrder);

        MenuSectionEventEntry reordered = Entry("reordered");
        Assert.Null(reordered.NewName);
        Assert.Null(reordered.NewDescription);
        Assert.Equal(2, reordered.NewDisplayOrder);

        foreach (string activation in new[] { "deactivated", "activated" })
        {
            MenuSectionEventEntry entry = Entry(activation);
            Assert.Null(entry.NewName);
            Assert.Null(entry.NewDescription);
            Assert.Null(entry.NewDisplayOrder);
        }
    }

    /// <summary>
    /// Clearing a description is an ordinary change with an ordinary event carrying <c>""</c> — not a
    /// deletion and not a null. That is the entire reason <c>menu_section.description</c> is
    /// <c>NOT NULL DEFAULT ''</c>: the paired CHECK could not tie an optional payload to its event type if
    /// clearing wrote NULL, and a surface distinguishing "cleared" from "set" reads the length rather than
    /// testing for null.
    /// </summary>
    [Fact]
    public async Task ListForSection_AClearedDescriptionIsAnEventCarryingTheEmptyString()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        await Administration().DescribeMenuSectionAsync(
            drinks, null, _administratorIdentifier, cancellationToken);

        MenuSectionEventEntry described = (await Log().ListForSectionAsync(drinks, cancellationToken))
            .Single(entry => entry.EventType == "described");

        // Compared against string.Empty rather than asserted NotNull-then-Empty: the distinction under
        // test is "" versus null, and one equality states it without depending on how an assertion
        // library annotates its parameters for flow analysis.
        Assert.Equal(string.Empty, described.NewDescription);
    }

    /// <summary>
    /// Who did it, rendered the way every other reader in this layer renders a person: the display name,
    /// falling back to the username when there is none rather than to blank.
    /// </summary>
    [Fact]
    public async Task ListForSection_NamesTheActorAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: false, _managerIdentifier, cancellationToken);

        IReadOnlyList<MenuSectionEventEntry> history =
            await Log().ListForSectionAsync(drinks, cancellationToken);

        MenuSectionEventEntry created = history.Single(entry => entry.EventType == "created");
        Assert.Equal(_administratorIdentifier, created.ActorPersonIdentifier);
        Assert.Equal("Adam Osei", created.ActorName);

        MenuSectionEventEntry hidden = history.Single(entry => entry.EventType == "deactivated");
        Assert.Equal(_managerIdentifier, hidden.ActorPersonIdentifier);
        Assert.Equal("moe", hidden.ActorName);
    }

    /// <summary>
    /// One heading's history is one heading's history. A reader that dropped the WHERE — or joined
    /// <c>menu_section</c> without qualifying <c>menu_section_identifier</c>, which is PostgreSQL error
    /// 42702 on a good day and a wrong answer on a bad one — would return the whole table here, and the
    /// page above renders whatever it is handed.
    /// </summary>
    [Fact]
    public async Task ListForSection_ReturnsNothingFromAnotherHeadingAndNothingForAnUnknownOne()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", null, cancellationToken);
        Guid puddings = await CreateAsync("Puddings", null, cancellationToken);

        await Administration().RenameMenuSectionAsync(
            puddings, "Sweets", _administratorIdentifier, cancellationToken);

        Assert.Single(await Log().ListForSectionAsync(drinks, cancellationToken));
        Assert.Equal(2, (await Log().ListForSectionAsync(puddings, cancellationToken)).Count);

        // An unknown identifier yields an empty list rather than an error: the heading may have existed
        // and the link may be stale, and the page above says so.
        Assert.Empty(await Log().ListForSectionAsync(_identifiers.Create(), cancellationToken));
    }

    /// <summary>
    /// A no-op writes no event, so it appears in no history — asserted here rather than only in
    /// <c>MenuSectionAdministrationTests</c> because this is the read somebody actually looks at. §11.4's
    /// per-section history is meant to be read by a person, and an append-only log of "somebody pressed
    /// Save" is noise.
    /// </summary>
    [Fact]
    public async Task ListForSection_ShowsNothingForAWriteThatChangedNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        await Administration().RenameMenuSectionAsync(
            drinks, "Drinks", _administratorIdentifier, cancellationToken);
        await Administration().DescribeMenuSectionAsync(
            drinks, "Cold things", _administratorIdentifier, cancellationToken);
        await Administration().ReorderMenuSectionAsync(
            drinks, 0, _administratorIdentifier, cancellationToken);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: true, _administratorIdentifier, cancellationToken);

        MenuSectionEventEntry only = Assert.Single(await Log().ListForSectionAsync(drinks, cancellationToken));
        Assert.Equal("created", only.EventType);
    }

    /// <summary>
    /// One heading, created through the write service so that its <c>created</c> event is the real one.
    /// <b>Every call writes exactly ONE row</b>, which is the opposite of what creating a menu item costs
    /// as of <c>0005</c> — a section's <c>created</c> carries all three payloads, an item's carries the
    /// name and the price and needs a <c>section_changed</c> beside it.
    /// </summary>
    private async Task<Guid> CreateAsync(
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, name, description, _administratorIdentifier, cancellationToken);

        return identifier;
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuSectionEventLog Log() => new(_connectionFactory!);

    private DapperMenuSectionAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);
}
