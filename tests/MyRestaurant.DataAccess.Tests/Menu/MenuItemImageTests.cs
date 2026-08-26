using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Menu;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuItemImageTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string AttachedEvent = "attached";

    private const string ReplacedEvent = "replaced";

    private const string RemovedEvent = "removed";

    private const string AltTextChangedEvent = "alt_text_changed";

    private const string CountImagesSql = """
        SELECT count(*)::int FROM menu_item_image;
        """;

    private const string CountEventsSql = """
        SELECT count(*)::int FROM menu_item_image_event;
        """;

    private const string ReadEventsSql = """
        SELECT event_type       AS EventType,
               new_content_type AS NewContentType,
               new_byte_length  AS NewByteLength,
               new_alt_text     AS NewAltText
        FROM menu_item_image_event
        WHERE menu_item_identifier = @MenuItemIdentifier
        ORDER BY occurred_at, menu_item_image_event_identifier;
        """;

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

        Assert.Equal(string.Empty, stored.AltText);

        ImageEvent[] expected = [new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length, null)];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

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

        Assert.Equal(1, await World().CountAsync(CountImagesSql, cancellationToken));
        Assert.Null(await Directory().ReadContentAsync(first, cancellationToken));

        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length, null),
            new(ReplacedEvent, ImageFormat.JpegContentType, JpegBytes.Length, null),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

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

        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length, null),
            new(RemovedEvent, null, null, null),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

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

        Assert.Null(await Directory().ReadContentAsync(_identifiers.Create(), cancellationToken));
    }

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

    [Fact]
    public async Task ACaptionIsStoredAndItsEventCarriesItWithoutMovingTheIdentifier()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid image = _identifiers.Create();

        await Administration().AttachMenuItemImageAsync(
            image, item, ImageFormat.PngContentType, PngBytes, _administratorIdentifier, cancellationToken);

        const string Caption = "Served on a bed of wilted greens with a lemon wedge";

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.Changed,
            await Administration().SetMenuItemImageAltTextAsync(
                item, Caption, _administratorIdentifier, cancellationToken));

        MenuItemImageMetadata? stored = await Directory().FindForItemAsync(item, cancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(Caption, stored!.AltText);

        Assert.Equal(image, stored.MenuItemImageIdentifier);
        Assert.NotNull(await Directory().ReadContentAsync(image, cancellationToken));

        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length, null),
            new(AltTextChangedEvent, null, null, Caption),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

    [Fact]
    public async Task ACaptionThatDidNotMoveWritesNothingAndAnEmptyOneClearsIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);

        await Administration().AttachMenuItemImageAsync(
            _identifiers.Create(),
            item,
            ImageFormat.PngContentType,
            PngBytes,
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.NoChange,
            await Administration().SetMenuItemImageAltTextAsync(
                item, string.Empty, _administratorIdentifier, cancellationToken));

        await Administration().SetMenuItemImageAltTextAsync(
            item, "Grilled, skin on", _administratorIdentifier, cancellationToken);

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.NoChange,
            await Administration().SetMenuItemImageAltTextAsync(
                item, "Grilled, skin on", _administratorIdentifier, cancellationToken));

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.Changed,
            await Administration().SetMenuItemImageAltTextAsync(
                item, string.Empty, _administratorIdentifier, cancellationToken));

        MenuItemImageMetadata? stored = await Directory().FindForItemAsync(item, cancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(string.Empty, stored!.AltText);

        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length, null),
            new(AltTextChangedEvent, null, null, "Grilled, skin on"),
            new(AltTextChangedEvent, null, null, string.Empty),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));
    }

    [Fact]
    public async Task ACaptionSurvivesAReplaceAndTheCarryWritesNoEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid item = await World().AddMenuItemAsync("Salmon", 18.50m, cancellationToken);
        Guid first = _identifiers.Create();
        Guid second = _identifiers.Create();

        await Administration().AttachMenuItemImageAsync(
            first, item, ImageFormat.PngContentType, PngBytes, _administratorIdentifier, cancellationToken);

        const string Caption = "Whole fillet, skin side up";

        await Administration().SetMenuItemImageAltTextAsync(
            item, Caption, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            AttachMenuItemImageOutcome.Replaced,
            (await Administration().AttachMenuItemImageAsync(
                second,
                item,
                ImageFormat.JpegContentType,
                JpegBytes,
                _administratorIdentifier,
                cancellationToken)).Outcome);

        MenuItemImageMetadata? stored = await Directory().FindForItemAsync(item, cancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(second, stored!.MenuItemImageIdentifier);
        Assert.Equal(Caption, stored.AltText);

        ImageEvent[] expected =
        [
            new(AttachedEvent, ImageFormat.PngContentType, PngBytes.Length, null),
            new(AltTextChangedEvent, null, null, Caption),
            new(ReplacedEvent, ImageFormat.JpegContentType, JpegBytes.Length, null),
        ];

        Assert.Equal(expected, await ReadHistoryAsync(item, cancellationToken));

        await Administration().RemoveMenuItemImageAsync(
            item, _administratorIdentifier, cancellationToken);

        Assert.Null(await Directory().FindForItemAsync(item, cancellationToken));
    }

    [Fact]
    public async Task ACaptionIsRefusedWithNoPictureAndWithNoItem()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid bare = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.NoImage,
            await Administration().SetMenuItemImageAltTextAsync(
                bare, "A bowl of soup", _administratorIdentifier, cancellationToken));

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.MenuItemNotFound,
            await Administration().SetMenuItemImageAltTextAsync(
                _identifiers.Create(), "Nothing", _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

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

    private sealed record ImageEvent(
        string EventType,
        string? NewContentType,
        int? NewByteLength,
        string? NewAltText);
}
