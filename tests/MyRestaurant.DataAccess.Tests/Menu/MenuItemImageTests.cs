using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Menu;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuItemImageAdministration"/> and
/// <see cref="DapperMenuItemImageDirectory"/> against a real PostgreSQL 17 container — §7's one picture per
/// menu item, which is Stage 4a of <c>docs/MENU_AND_HANDHELD_PLAN.md</c>.
///
/// <para><b>Its own class rather than facts on <see cref="MenuAdministrationTests"/>, and the reason is
/// what these tests are about.</b> Every verb over there writes one row and one event on <c>menu_item</c>
/// and <c>menu_item_event</c>, and every helper it has is built on that. These two verbs <em>delete</em> a
/// row — a replace mints a new identifier and drops the old one, a removal drops it outright — so the facts
/// worth pinning are about what survives the deletion, which is a different question about a different
/// table.</para>
///
/// <para><b>Three of these are about what is NOT stored, and they are the point.</b> A picture that is not
/// the format it claims to be would be served from this application's own origin under the header the
/// column says (§7); a replace that reused the identifier would make <c>Cache-Control: immutable</c> a
/// false statement and leave last week's photograph on every phone that has the menu open; and a removal
/// that took the history with the bytes would leave §11.4 unable to say a picture had ever been there.
/// Each of those fails silently and leaves an artefact that reads plausibly, which is the worse of the two
/// failures in an append-only system (ADR-0002).</para>
///
/// <para><see cref="EveryContentTypeTheDomainRecognisesIsOneTheSchemaAdmits"/> is the one worth reading.
/// §8.2 declares the media-type vocabulary in a CHECK and <see cref="ImageFormat"/> holds a second copy of
/// it in C#, which is <b>F-80's shape exactly</b> — and the repair there was a gate that read the SQL text.
/// This is stronger: it attaches a real file of every format the domain can identify and requires the
/// database to take it, so the two agreeing on paper while nothing can actually be stored is also a
/// failure. The reverse direction needs no assertion and is safe by construction — a type the CHECK admits
/// and the domain cannot identify is a type no caller can produce bytes for, because the write refuses the
/// pair before it opens a transaction.</para>
///
/// <para><b>The size cap is never written down in this file.</b> §8.2 declares it in a named CHECK and
/// <see cref="AttachMenuItemImageOutcome.BytesOverCap"/> is reported by reading that constraint's name off
/// the PostgreSQL error, so <see cref="BytesOverTheSchemasCapAreRefusedWithNothingWritten"/> finds the
/// bound by <em>asking the database for its own constraint definition</em> rather than by restating a
/// number this file would then own a second copy of (F-101). A migration that moves the cap moves this
/// test with it instead of turning it red.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class MenuItemImageTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    /// <summary>Stored spellings of <c>menu_item_image_event.event_type</c> (§8.2's CHECK).</summary>
    private const string AttachedEvent = "attached";

    private const string ReplacedEvent = "replaced";

    private const string RemovedEvent = "removed";

    private const string CountImagesSql = """
        SELECT count(*)::int FROM menu_item_image;
        """;

    private const string CountEventsSql = """
        SELECT count(*)::int FROM menu_item_image_event;
        """;

    /// <summary>
    /// The whole log for one item, oldest first, with both payload columns. There is deliberately no
    /// <c>IMenuItemImageEventLog</c> to read it through yet — §11.4 has no panel for it, and a read with no
    /// caller is the defect this project keeps recording about workflow verbs — so the facts that need the
    /// history read the table, which is also the arrangement that proves the rows are written at all.
    /// </summary>
    private const string ReadEventsSql = """
        SELECT event_type       AS EventType,
               new_content_type AS NewContentType,
               new_byte_length  AS NewByteLength
        FROM menu_item_image_event
        WHERE menu_item_identifier = @MenuItemIdentifier
        ORDER BY occurred_at, menu_item_image_event_identifier;
        """;

    /// <summary>
    /// §8.2's cap, asked for rather than restated. <c>pg_get_constraintdef</c> renders the CHECK as
    /// PostgreSQL stores it — <c>CHECK ((octet_length(bytes) &lt;= 524288))</c> — and the only run of digits
    /// in it is the bound, so the number in the migration is the number this file uses.
    /// </summary>
    private const string ReadByteCapSql = """
        SELECT (regexp_match(pg_get_constraintdef(pg_constraint.oid), '([0-9]+)'))[1]::int
        FROM pg_constraint
        WHERE pg_constraint.conname = 'menu_item_image_bytes_within_cap';
        """;

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static readonly byte[] WebPBytes =
    [
        0x52, 0x49, 0x46, 0x46,
        0x1A, 0x00, 0x00, 0x00,
        0x57, 0x45, 0x42, 0x50,
        0x56, 0x50, 0x38, 0x20,
    ];

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 18, 12, 30, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    public MenuItemImageTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        // TRUNCATE … CASCADE on menu_item reaches both of 0006's tables, since both reference it. Nothing
        // in OrderTestWorld needed an edit for this migration, which is the property the stage was cut for.
        await _world.TruncateAsync(cancellationToken);

        _administratorIdentifier = await _world.AddPersonAsync("adam", "Adam", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    /// <summary>
    /// The row and its event, written together, and the metadata read back with a length nobody stored.
    /// </summary>
    [Fact]
    public async Task AttachingStoresThePictureAndWritesAnAttachedEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid image = _identifiers.Create();

        AttachMenuItemImageResult result = await Administration().AttachMenuItemImageAsync(
            image, item, ImageFormat.PngContentType, PngBytes, _administratorIdentifier, cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.Attached, result.Outcome);
        Assert.Equal(image, result.MenuItemImageIdentifier);

        MenuItemImageMetadata? stored = await Directory().FindForItemAsync(item, cancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(image, stored!.MenuItemImageIdentifier);
        Assert.Equal(ImageFormat.PngContentType, stored.ContentType);
        Assert.Equal(PngBytes.Length, stored.ByteLength);
        Assert.Equal(_clock.UtcNow, stored.UploadedAt);

        ImageEvent[] expected = [new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length)];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

    /// <summary>
    /// <b>The identifier must change</b>, because §7's route is keyed on it and
    /// <c>Cache-Control: immutable</c> is a true statement only while a URL names one set of bytes forever.
    /// An implementation that updated the bytes under the stored identifier passes every other fact in this
    /// file.
    /// </summary>
    [Fact]
    public async Task ReplacingMintsANewIdentifierAndLeavesExactlyOnePicture()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid first = _identifiers.Create();
        Guid second = _identifiers.Create();

        await Administration().AttachMenuItemImageAsync(
            first, item, ImageFormat.PngContentType, PngBytes, _administratorIdentifier, cancellationToken);

        AttachMenuItemImageResult result = await Administration().AttachMenuItemImageAsync(
            second,
            item,
            ImageFormat.JpegContentType,
            JpegBytes,
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.Replaced, result.Outcome);
        Assert.Equal(second, result.MenuItemImageIdentifier);
        Assert.NotEqual(first, second);

        MenuItemImageMetadata? stored = await Directory().FindForItemAsync(item, cancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(second, stored!.MenuItemImageIdentifier);
        Assert.Equal(ImageFormat.JpegContentType, stored.ContentType);

        // One picture per item, and the superseded row is gone rather than orphaned — so the URL that
        // named it answers with nothing, which is §7's 404 for a stale image link.
        Assert.Equal(1, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Null(await Directory().ReadContentAsync(first, cancellationToken));

        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length),
            new(ReplacedEvent, ImageFormat.JpegContentType, JpegBytes.Length),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

    /// <summary>
    /// The row goes and the history stays, which is the whole argument for §6.8's hide-never-delete rule
    /// having a stated exception here: what a reader wants is that a picture was there, what it was, and
    /// who removed it — none of which is in the bytes.
    /// </summary>
    [Fact]
    public async Task RemovingDeletesTheRowAndLeavesEveryEventBehind()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid image = _identifiers.Create();

        await Administration().AttachMenuItemImageAsync(
            image, item, ImageFormat.PngContentType, PngBytes, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            RemoveMenuItemImageOutcome.Removed,
            await Administration().RemoveMenuItemImageAsync(
                item, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Null(await Directory().FindForItemAsync(item, cancellationToken));
        Assert.Null(await Directory().ReadContentAsync(image, cancellationToken));

        // The attach's payload survives the bytes it describes, and the removal carries neither column,
        // which is what §8.2's two biconditionals require of that type.
        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length),
            new(RemovedEvent, null, null),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

    /// <summary>
    /// The no-op rule every menu verb follows, one register over: an item with no picture is not an error
    /// and is not an event either, because §11.4's history is meant to be read by a person and an
    /// append-only log of "somebody pressed Remove" is noise.
    /// </summary>
    [Fact]
    public async Task RemovingWhenNothingIsAttachedWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        Assert.Equal(
            RemoveMenuItemImageOutcome.NoImage,
            await Administration().RemoveMenuItemImageAsync(
                item, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// Both verbs against an item this menu does not hold. The existence check is a locking read inside the
    /// transaction rather than a prior query, so this also pins that neither verb can be talked into
    /// referencing a row it never confirmed.
    /// </summary>
    [Fact]
    public async Task AnUnknownItemIsRefusedByBothVerbsWithNothingWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid absent = _identifiers.Create();

        AttachMenuItemImageResult attach = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            absent,
            ImageFormat.PngContentType,
            PngBytes,
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.MenuItemNotFound, attach.Outcome);
        Assert.Null(attach.MenuItemImageIdentifier);

        Assert.Equal(
            RemoveMenuItemImageOutcome.MenuItemNotFound,
            await Administration().RemoveMenuItemImageAsync(
                absent, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// The finding <see cref="ImageFormat"/> exists for, at the layer that stores the claim: a real JPEG
    /// declared as a PNG is refused, because §7's route sets the response's <c>Content-Type</c> from the
    /// stored column and a column that disagrees with its own bytes makes this application mislabel its own
    /// responses on its own origin.
    /// </summary>
    [Fact]
    public async Task BytesThatContradictTheDeclaredTypeAreRefused()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        AttachMenuItemImageResult result = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            item,
            ImageFormat.PngContentType,
            JpegBytes,
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.ContentTypeContradictedByBytes, result.Outcome);
        Assert.Equal(0, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// A media type outside §8.2's vocabulary, and an empty upload, are separate answers — and the empty one
    /// is refused <em>first</em>, so an operator who picked a zero-byte file is told that rather than being
    /// told their PNG is not a PNG. GIF is the interesting refusal: it is a perfectly good picture, excluded
    /// by the vocabulary rather than by being unrecognisable.
    /// </summary>
    [Fact]
    public async Task AnUnsupportedTypeAndAnEmptyUploadGetDifferentAnswers()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        AttachMenuItemImageResult unsupported = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            item,
            "image/gif",
            [0x47, 0x49, 0x46, 0x38, 0x39, 0x61],
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.UnsupportedContentType, unsupported.Outcome);

        AttachMenuItemImageResult empty = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            item,
            ImageFormat.PngContentType,
            [],
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.BytesEmpty, empty.Outcome);

        Assert.Equal(0, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// <b>The cap is the database's and this fact asks for it.</b> One byte over is refused with nothing
    /// written; the cap exactly is accepted, because a bound nobody can reach is a bound stated one off.
    /// </summary>
    [Fact]
    public async Task BytesOverTheSchemasCapAreRefusedWithNothingWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        int? declaredCap = await World().ScalarAsync<int>(ReadByteCapSql, null, cancellationToken);

        Assert.NotNull(declaredCap);
        Assert.True(
            declaredCap > PngBytes.Length,
            $"§8.2's cap read back as {declaredCap}, which is not a cap on anything.");

        int cap = declaredCap!.Value;

        AttachMenuItemImageResult over = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            item,
            ImageFormat.PngContentType,
            PaddedPng(cap + 1),
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.BytesOverCap, over.Outcome);
        Assert.Equal(0, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));

        AttachMenuItemImageResult exactly = await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            item,
            ImageFormat.PngContentType,
            PaddedPng(cap),
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.Attached, exactly.Outcome);
    }

    /// <summary>
    /// The route's read, and the only fact in this file that touches the bytes. Byte-identical, because §7
    /// stores what it is given: nothing in this stack decodes, resizes or re-encodes an upload, so a
    /// difference of one byte here means something is rewriting a file it was told to keep.
    /// </summary>
    [Fact]
    public async Task TheContentReadBackIsByteIdenticalToWhatWasStored()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid image = _identifiers.Create();
        byte[] uploaded = PaddedPng(4096);

        await Administration().AttachMenuItemImageAsync(
            image, item, ImageFormat.PngContentType, uploaded, _administratorIdentifier, cancellationToken);

        MenuItemImageContent? content = await Directory().ReadContentAsync(image, cancellationToken);

        Assert.NotNull(content);
        Assert.Equal(image, content!.MenuItemImageIdentifier);
        Assert.Equal(ImageFormat.PngContentType, content.ContentType);
        Assert.Equal(uploaded, content.Bytes);

        // An identifier this table does not hold is a null rather than an error.
        Assert.Null(await Directory().ReadContentAsync(_identifiers.Create(), cancellationToken));
    }

    /// <summary>
    /// The read §11.1's guest menu and §11.4's index will use, which is a list of what is decorated rather
    /// than a left join over the whole menu: an item with no picture is absent, and an item that had one and
    /// lost it is absent again.
    /// </summary>
    [Fact]
    public async Task TheDirectoryListsOnlyTheItemsThatHaveAPicture()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid decorated = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid bare = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        Guid stripped = await World().AddMenuItemAsync("Tart", 6.00m, cancellationToken);

        await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            decorated,
            ImageFormat.PngContentType,
            PngBytes,
            _administratorIdentifier,
            cancellationToken);

        await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            stripped,
            ImageFormat.WebPContentType,
            WebPBytes,
            _administratorIdentifier,
            cancellationToken);

        await Administration().RemoveMenuItemImageAsync(
            stripped, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuItemImageMetadata> listed = await Directory().ListAsync(cancellationToken);

        Assert.Equal([decorated], listed.Select(metadata => metadata.MenuItemIdentifier));
        Assert.DoesNotContain(bare, listed.Select(metadata => metadata.MenuItemIdentifier));
    }

    /// <summary>
    /// F-80's shape, gated behaviourally. The sample bytes are held in a dictionary keyed by the type they
    /// produce, and the dictionary's key set is asserted equal to
    /// <see cref="ImageFormat.RecognisedContentTypes"/> <em>first</em>, because a fact that walked a set it
    /// had no sample for would silently walk a shorter one (F-41).
    /// </summary>
    [Fact]
    public async Task EveryContentTypeTheDomainRecognisesIsOneTheSchemaAdmits()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Dictionary<string, byte[]> samples = new(StringComparer.Ordinal)
        {
            [ImageFormat.PngContentType] = PngBytes,
            [ImageFormat.JpegContentType] = JpegBytes,
            [ImageFormat.WebPContentType] = WebPBytes,
        };

        Assert.Equal(
            ImageFormat.RecognisedContentTypes.Order(StringComparer.Ordinal).ToArray(),
            samples.Keys.Order(StringComparer.Ordinal).ToArray());

        foreach (string contentType in ImageFormat.RecognisedContentTypes)
        {
            Guid item = await World().AddMenuItemAsync($"Dish {contentType}", 9.00m, cancellationToken);

            AttachMenuItemImageResult result = await Administration().AttachMenuItemImageAsync(
                _identifiers.Create(),
                item,
                contentType,
                samples[contentType],
                _administratorIdentifier,
                cancellationToken);

            Assert.Equal(AttachMenuItemImageOutcome.Attached, result.Outcome);
        }

        Assert.Equal(
            ImageFormat.RecognisedContentTypes.Count,
            await World().CountAsync(CountImagesSql, cancellationToken));
    }

    /// <summary>
    /// A PNG whose signature is real and whose remainder is padding, at an exact total length. The write
    /// reads the first eight bytes and no more (§7), so padding is the honest way to reach a size without
    /// committing a large binary to this repository.
    /// </summary>
    private static byte[] PaddedPng(int totalByteLength)
    {
        byte[] bytes = new byte[totalByteLength];
        PngBytes.AsSpan(0, Math.Min(PngBytes.Length, totalByteLength)).CopyTo(bytes);
        return bytes;
    }

    private async Task<IReadOnlyList<ImageEvent>> ReadHistoryAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await World().QueryAsync<ImageEvent>(
            ReadEventsSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(
            _fixture.ConnectionString is not null,
            _fixture.SkipReason ?? "No container engine.");

    private DapperMenuItemImageAdministration Administration()
        => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuItemImageDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;

    /// <summary>
    /// One row of <c>menu_item_image_event</c>, as a record so that <c>Assert.Equal</c> compares whole
    /// events by value rather than one column at a time. A positional record is safe here where it would
    /// not be for a stored timestamp: no member is a <see cref="DateTimeOffset"/>, so Npgsql's
    /// <c>timestamptz</c> materialisation is not in play.
    /// </summary>
    private sealed record ImageEvent(
        string EventType,
        string? NewContentType,
        int? NewByteLength);
}
