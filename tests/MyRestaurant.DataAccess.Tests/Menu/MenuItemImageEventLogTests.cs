using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Menu;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuItemImageEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 19, 10, 15, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

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

        Assert.All(history, entry => Assert.Equal("Salmon", entry.MenuItemName));
    }

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

        MenuItemImageEventEntry Entry(string eventType) => history.Single(entry => entry.EventType == eventType);

        MenuItemImageEventEntry attached = Entry("attached");
        Assert.Equal(ImageFormat.PngContentType, attached.NewContentType);
        Assert.Equal(PngBytes.Length, attached.NewByteLength);
        Assert.Null(attached.NewAltText);

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

        Assert.Null(await Directory().FindForItemAsync(salmon, cancellationToken));

        IReadOnlyList<MenuItemImageEventEntry> history =
            await Log().ListForItemAsync(salmon, cancellationToken);

        Assert.Equal(3, history.Count);
        Assert.Equal(
            new[] { first, second, second },
            history.Select(entry => entry.MenuItemImageIdentifier).ToArray());

        Assert.NotEqual(first, second);

        Assert.Null(await Directory().ReadContentAsync(first, cancellationToken));
        Assert.Null(await Directory().ReadContentAsync(second, cancellationToken));
    }

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

        Assert.Equal(2, captions.Length);
        Assert.Equal("On a bed of wilted greens", captions[0].NewAltText);
        Assert.Equal(string.Empty, captions[1].NewAltText);
    }

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

        Assert.Empty(await Log().ListForItemAsync(soup, cancellationToken));
        Assert.Empty(await Log().ListForItemAsync(_identifiers.Create(), cancellationToken));
    }

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
