using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;
    private Guid _kitchenIdentifier;

    private Guid _sectionIdentifier;

    public MenuEventLogTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _kitchenIdentifier = await _world.AddPersonAsync("kim", null, cancellationToken);
        _sectionIdentifier = await _world.AddMenuSectionAsync("Mains", cancellationToken);
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

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RenameMenuItemAsync(
            soup, "Soup of the day", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RepriceMenuItemAsync(soup, 5.00m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Availability().SetActiveAsync(soup, isActive: true, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        Assert.Equal(
            new[] { "created", "section_changed", "name_changed", "price_changed", "deactivated", "activated" },
            history.Select(entry => entry.EventType).ToArray());

        Assert.Equal(history.OrderBy(entry => entry.OccurredAt).ToArray(), history.ToArray());
        Assert.All(history, entry => Assert.Equal(soup, entry.MenuItemIdentifier));
        Assert.All(history, entry => Assert.Equal("Soup of the day", entry.MenuItemName));
    }

    [Fact]
    public async Task ListForItem_CarriesExactlyThePayloadEachEventTypeIsAllowed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        await Administration().RenameMenuItemAsync(soup, "Broth", _administratorIdentifier, cancellationToken);
        await Administration().RepriceMenuItemAsync(soup, 5.00m, _administratorIdentifier, cancellationToken);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);
        await Availability().SetActiveAsync(soup, isActive: true, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        MenuItemEventEntry Entry(string eventType) => history.Single(entry => entry.EventType == eventType);

        MenuItemEventEntry created = Entry("created");
        Assert.Equal("Soup", created.NewName);
        Assert.Equal(4.50m, created.NewPriceAmount);

        MenuItemEventEntry filed = Entry("section_changed");
        Assert.Equal(_sectionIdentifier, filed.NewMenuSectionIdentifier);
        Assert.Equal("Mains", filed.NewMenuSectionName);
        Assert.Null(filed.NewName);
        Assert.Null(filed.NewPriceAmount);

        Assert.Null(created.NewMenuSectionIdentifier);
        Assert.Null(created.NewMenuSectionName);

        MenuItemEventEntry renamed = Entry("name_changed");
        Assert.Equal("Broth", renamed.NewName);
        Assert.Null(renamed.NewPriceAmount);

        MenuItemEventEntry repriced = Entry("price_changed");
        Assert.Null(repriced.NewName);
        Assert.Equal(5.00m, repriced.NewPriceAmount);

        foreach (string flip in new[] { "deactivated", "activated" })
        {
            MenuItemEventEntry entry = Entry(flip);
            Assert.Null(entry.NewName);
            Assert.Null(entry.NewPriceAmount);
        }
    }

    [Fact]
    public async Task ListForItem_NamesTheActorAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        MenuItemEventEntry created = history.Single(entry => entry.EventType == "created");
        Assert.Equal(_administratorIdentifier, created.ActorPersonIdentifier);
        Assert.Equal("Adam Osei", created.ActorName);

        MenuItemEventEntry deactivated = history.Single(entry => entry.EventType == "deactivated");
        Assert.Equal(_kitchenIdentifier, deactivated.ActorPersonIdentifier);
        Assert.Equal("kim", deactivated.ActorName);
    }

    [Fact]
    public async Task ListForItem_ReadsTheInstantBackAsUtc()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DateTimeOffset createdAt = _clock.UtcNow;
        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        Assert.Equal(2, history.Count);
        Assert.All(history, entry => Assert.Equal(createdAt, entry.OccurredAt));
        Assert.All(history, entry => Assert.Equal(TimeSpan.Zero, entry.OccurredAt.Offset));
    }

    [Fact]
    public async Task ListForItem_OnlyReturnsThatItemsEvents()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        Guid steak = await CreateAsync("Steak", 21.00m, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> soupHistory =
            await Log().ListForItemAsync(soup, cancellationToken);

        Assert.Equal(2, soupHistory.Count);
        Assert.All(soupHistory, entry => Assert.Equal(soup, entry.MenuItemIdentifier));
        Assert.DoesNotContain(soupHistory, entry => entry.MenuItemIdentifier == steak);
    }

    [Fact]
    public async Task ListForItem_AnUnknownItemIsEmptyRatherThanAnError()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Empty(await Log().ListForItemAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task ListRecent_IsNewestFirstAcrossItemsAndRespectsTheCap()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        Guid steak = await CreateAsync("Steak", 21.00m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> recent = await Log().ListRecentAsync(2, cancellationToken);

        Assert.Equal(2, recent.Count);
        Assert.Equal("deactivated", recent[0].EventType);
        Assert.Equal(soup, recent[0].MenuItemIdentifier);

        Assert.Equal("section_changed", recent[1].EventType);
        Assert.Equal(steak, recent[1].MenuItemIdentifier);

        Assert.Equal(5, (await Log().ListRecentAsync(50, cancellationToken)).Count);
    }

    [Fact]
    public async Task ListRecent_ANonPositiveCapReturnsNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await CreateAsync("Soup", 4.50m, cancellationToken);

        Assert.Empty(await Log().ListRecentAsync(0, cancellationToken));
        Assert.Empty(await Log().ListRecentAsync(-5, cancellationToken));
    }

    [Fact]
    public async Task ListRecent_ShowsTheCurrentItemNameBesideTheNameEachEventSet()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RenameMenuItemAsync(soup, "Broth", _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> recent = await Log().ListRecentAsync(10, cancellationToken);

        Assert.All(recent, entry => Assert.Equal("Broth", entry.MenuItemName));

        Assert.Equal("Broth", recent[0].NewName);
        Assert.Equal("Soup", recent.Single(entry => entry.EventType == "created").NewName);
    }

    private async Task<Guid> CreateAsync(string name, decimal price, CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier,
            _sectionIdentifier,
            name,
            description: null,
            price,
            _administratorIdentifier,
            cancellationToken);

        return identifier;
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuEventLog Log() => new(_connectionFactory!);

    private DapperMenuAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuAvailability Availability() => new(_connectionFactory!, _clock, _identifiers);
}
