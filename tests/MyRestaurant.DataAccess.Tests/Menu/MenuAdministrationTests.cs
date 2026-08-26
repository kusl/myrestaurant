using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuAdministrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountEventsSql = """
        SELECT count(*)::int FROM menu_item_event;
        """;

    private const string CountItemsSql = """
        SELECT count(*)::int FROM menu_item;
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 18, 16, 45, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    private Guid _sectionIdentifier;

    public MenuAdministrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task CreateWritesTheItemAndItsCreatedEventTogether()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Salmon", description: null, 18.00m, _administratorIdentifier, cancellationToken);

        Assert.Equal(identifier, result.MenuItemIdentifier);
        Assert.Equal("Salmon", result.Name);
        Assert.Equal(18.00m, result.PriceAmount);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Salmon", stored.Name);
        Assert.Equal(18.00m, stored.PriceAmount);

        Assert.True(stored.IsActive);
        Assert.Equal(_clock.UtcNow, stored.CreatedAt);

        Assert.Equal(_sectionIdentifier, stored.MenuSectionIdentifier);
        Assert.Equal("Mains", stored.MenuSectionName);
        Assert.Equal("Mains", result.MenuSectionName);

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("section_changed", await EventTypeAsync(identifier, cancellationToken));

        Guid? loggedSection = await ScalarAsync<Guid?>(
            "new_menu_section_identifier", identifier, cancellationToken);
        Assert.Equal(_sectionIdentifier, loggedSection);

        string? createdName = await World().ScalarAsync<string>(
            """
            SELECT new_name
            FROM menu_item_event
            WHERE menu_item_identifier = @MenuItemIdentifier AND event_type = 'created';
            """,
            new { MenuItemIdentifier = identifier },
            cancellationToken);

        decimal? createdPrice = await World().ScalarAsync<decimal?>(
            """
            SELECT new_price_amount
            FROM menu_item_event
            WHERE menu_item_identifier = @MenuItemIdentifier AND event_type = 'created';
            """,
            new { MenuItemIdentifier = identifier },
            cancellationToken);

        Assert.Equal("Salmon", createdName);
        Assert.Equal(18.00m, createdPrice);
    }

    [Fact]
    public async Task CreatingUnderASectionThatDoesNotExistWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier,
            _identifiers.Create(),
            "Salmon",
            description: null,
            18.00m,
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal(CreateMenuItemOutcome.MenuSectionNotFound, result.Outcome);
        Assert.False(result.Created);
        Assert.Null(result.Name);
        Assert.Null(result.MenuSectionName);
        Assert.Null(result.DisplayOrder);

        Assert.Equal(0, await World().CountAsync(CountItemsSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Null(await Directory().GetAsync(identifier, cancellationToken));
    }

    [Fact]
    public async Task ItemsAreAppendedToTheEndOfTheirOwnSection()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);

        CreateMenuItemResult first = await Administration().CreateMenuItemAsync(
            _identifiers.Create(), _sectionIdentifier, "Soup", null, 4.50m,
            _administratorIdentifier, cancellationToken);
        CreateMenuItemResult second = await Administration().CreateMenuItemAsync(
            _identifiers.Create(), _sectionIdentifier, "Pie", null, 9.00m,
            _administratorIdentifier, cancellationToken);
        CreateMenuItemResult elsewhere = await Administration().CreateMenuItemAsync(
            _identifiers.Create(), puddings, "Trifle", null, 5.00m,
            _administratorIdentifier, cancellationToken);

        Assert.Equal(0, first.DisplayOrder);
        Assert.Equal(1, second.DisplayOrder);
        Assert.Equal(0, elsewhere.DisplayOrder);

        MenuItemSummary? storedSecond = await Directory().GetAsync(second.MenuItemIdentifier, cancellationToken);
        Assert.NotNull(storedSecond);
        Assert.Equal(1, storedSecond.DisplayOrder);
    }

    [Fact]
    public async Task AppendingUsesTheHighestPositionRatherThanTheCount()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddMenuItemAsync(
            "Soup", 4.50m, cancellationToken, displayOrder: 5, menuSectionIdentifier: _sectionIdentifier);

        CreateMenuItemResult appended = await Administration().CreateMenuItemAsync(
            _identifiers.Create(), _sectionIdentifier, "Pie", null, 9.00m,
            _administratorIdentifier, cancellationToken);

        Assert.Equal(6, appended.DisplayOrder);
    }

    [Fact]
    public async Task TheCreatedEventRecordsTheActorAndTheInstant()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Salmon", description: null, 18.00m, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            _administratorIdentifier,
            await ScalarAsync<Guid>("actor_person_identifier", identifier, cancellationToken));

        DateTime occurredAt = await ScalarAsync<DateTime>("occurred_at", identifier, cancellationToken);
        Assert.Equal(_clock.UtcNow.UtcDateTime, DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc));
    }

    [Fact]
    public async Task CreateRoundsToTheStoredScaleInBothRows()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Soup", description: null, 4.567m, _administratorIdentifier, cancellationToken);

        Assert.Equal(4.57m, result.PriceAmount);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(4.57m, stored.PriceAmount);

        Assert.Equal(
            4.57m,
            await CreatedEventScalarAsync<decimal>("new_price_amount", identifier, cancellationToken));
    }

    [Fact]
    public async Task CreateTrimsTheName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "  Soup  ", description: null, 4.50m, _administratorIdentifier, cancellationToken);

        Assert.Equal("Soup", result.Name);
        Assert.Equal(
            "Soup",
            await CreatedEventScalarAsync<string>("new_name", identifier, cancellationToken));
    }

    [Fact]
    public async Task TwoItemsMayShareAName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Administration().CreateMenuItemAsync(
            _identifiers.Create(), _sectionIdentifier, "Soup", description: null, 4.50m, _administratorIdentifier, cancellationToken);
        await Administration().CreateMenuItemAsync(
            _identifiers.Create(), _sectionIdentifier, "Soup", description: null, 5.50m, _administratorIdentifier, cancellationToken);

        Assert.Equal(2, await World().CountAsync(CountItemsSql, cancellationToken));
        Assert.Equal(2, (await Directory().ListAsync(cancellationToken)).Count);
    }

    [Fact]
    public async Task RenameChangesTheNameAndLogsNameChangedWithNoPrice()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        RenameMenuItemResult result = await Administration().RenameMenuItemAsync(
            soup, "Soup of the day", _administratorIdentifier, cancellationToken);

        Assert.Equal(RenameMenuItemOutcome.Renamed, result.Outcome);
        Assert.True(result.Changed);
        Assert.True(result.ItemExists);
        Assert.Equal("Soup of the day", result.Name);
        Assert.Equal("Soup", result.PreviousName);

        MenuItemSummary? stored = await Directory().GetAsync(soup, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Soup of the day", stored.Name);

        Assert.Equal(4.50m, stored.PriceAmount);
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("name_changed", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal("Soup of the day", await ScalarAsync<string>("new_name", soup, cancellationToken));
        Assert.Null(await ScalarAsync<decimal?>("new_price_amount", soup, cancellationToken));
    }

    [Fact]
    public async Task RenamingToTheSameNameWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        RenameMenuItemResult result = await Administration().RenameMenuItemAsync(
            soup, "  Soup  ", _administratorIdentifier, cancellationToken);

        Assert.Equal(RenameMenuItemOutcome.NoChange, result.Outcome);
        Assert.False(result.Changed);
        Assert.True(result.ItemExists);
        Assert.Equal("Soup", result.Name);
        Assert.Equal("Soup", result.PreviousName);

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RenamingOnlyTheCaseIsStillAChange()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("soup", 4.50m, cancellationToken);

        RenameMenuItemResult result = await Administration().RenameMenuItemAsync(
            soup, "Soup", _administratorIdentifier, cancellationToken);

        Assert.Equal(RenameMenuItemOutcome.Renamed, result.Outcome);
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RenamingAnUnknownItemReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RenameMenuItemResult result = await Administration().RenameMenuItemAsync(
            _identifiers.Create(), "Soup", _administratorIdentifier, cancellationToken);

        Assert.Equal(RenameMenuItemOutcome.MenuItemNotFound, result.Outcome);
        Assert.False(result.ItemExists);
        Assert.False(result.Changed);
        Assert.Null(result.Name);
        Assert.Null(result.PreviousName);

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RepriceChangesThePriceAndLogsPriceChangedWithNoName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        RepriceMenuItemResult result = await Administration().RepriceMenuItemAsync(
            soup, 5.25m, _administratorIdentifier, cancellationToken);

        Assert.Equal(RepriceMenuItemOutcome.Repriced, result.Outcome);
        Assert.True(result.Changed);
        Assert.True(result.ItemExists);
        Assert.Equal("Soup", result.Name);
        Assert.Equal(5.25m, result.PriceAmount);
        Assert.Equal(4.50m, result.PreviousPriceAmount);

        MenuItemSummary? stored = await Directory().GetAsync(soup, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(5.25m, stored.PriceAmount);

        Assert.Equal("Soup", stored.Name);
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("price_changed", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal(5.25m, await ScalarAsync<decimal>("new_price_amount", soup, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_name", soup, cancellationToken));
    }

    [Fact]
    public async Task RepricingToTheSameNumberAtADifferentScaleIsANoOp()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        RepriceMenuItemResult result = await Administration().RepriceMenuItemAsync(
            soup, 4.500m, _administratorIdentifier, cancellationToken);

        Assert.Equal(RepriceMenuItemOutcome.NoChange, result.Outcome);
        Assert.False(result.Changed);
        Assert.Equal(4.50m, result.PriceAmount);
        Assert.Equal(4.50m, result.PreviousPriceAmount);

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RepricingToZeroIsAllowed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid water = await World().AddMenuItemAsync("Tap water", 1.00m, cancellationToken);

        RepriceMenuItemResult result = await Administration().RepriceMenuItemAsync(
            water, 0m, _administratorIdentifier, cancellationToken);

        Assert.Equal(RepriceMenuItemOutcome.Repriced, result.Outcome);
        Assert.Equal(0m, result.PriceAmount);
        Assert.Equal(0m, await ScalarAsync<decimal>("new_price_amount", water, cancellationToken));
    }

    [Fact]
    public async Task RepricingAnUnknownItemReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RepriceMenuItemResult result = await Administration().RepriceMenuItemAsync(
            _identifiers.Create(), 5.00m, _administratorIdentifier, cancellationToken);

        Assert.Equal(RepriceMenuItemOutcome.MenuItemNotFound, result.Outcome);
        Assert.False(result.ItemExists);
        Assert.Null(result.PriceAmount);
        Assert.Null(result.PreviousPriceAmount);

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AnImpossiblePriceIsRefusedBeforeAnythingIsWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Administration().RepriceMenuItemAsync(
                soup, -0.01m, _administratorIdentifier, cancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Administration().RepriceMenuItemAsync(
                soup, 100_000_000m, _administratorIdentifier, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => Administration().RenameMenuItemAsync(
                soup, "   ", _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(4.50m, (await Directory().GetAsync(soup, cancellationToken))!.PriceAmount);
    }

    [Fact]
    public async Task TheHistoryKeepsEveryChangeFromBothWriteServices()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Soup", description: null, 4.50m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().RenameMenuItemAsync(
            identifier, "Soup of the day", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().RepriceMenuItemAsync(
            identifier, 5.00m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Availability().SetActiveAsync(
            identifier, isActive: false, _administratorIdentifier, cancellationToken);

        Assert.Equal(5, await World().CountAsync(CountEventsSql, cancellationToken));

        string? latest = await World().ScalarAsync<string>(
            """
            SELECT event_type
            FROM menu_item_event
            WHERE menu_item_identifier = @MenuItemIdentifier
            ORDER BY occurred_at DESC
            LIMIT 1;
            """,
            new { MenuItemIdentifier = identifier },
            cancellationToken);

        Assert.Equal("deactivated", latest);
    }

    [Fact]
    public async Task CreatingWithADescriptionWritesThreeEventsInOneTransaction()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier,
            "Salmon",
            "  Pan seared, with greens  ",
            18.00m,
            _administratorIdentifier,
            cancellationToken);

        Assert.Equal("Pan seared, with greens", result.Description);
        Assert.True(result.DescriptionWasSet);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Pan seared, with greens", stored.Description);

        Assert.Equal(0, stored.DisplayOrder);

        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));

        Assert.Equal("description_changed", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            "Pan seared, with greens",
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
    }

    [Fact]
    public async Task CreatingWithABlankDescriptionWritesNoDescriptionEventAndStoresTheEmptyString()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Tea", "   ", 2.00m, _administratorIdentifier, cancellationToken);

        Assert.Equal(string.Empty, result.Description);
        Assert.False(result.DescriptionWasSet);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(string.Empty, stored.Description);

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("section_changed", await EventTypeAsync(identifier, cancellationToken));

        Assert.Equal(
            0,
            await World().CountAsync(
                """
                SELECT count(*)::int
                FROM menu_item_event
                WHERE event_type = 'description_changed';
                """,
                cancellationToken));
    }

    [Fact]
    public async Task DescribeWritesTheColumnAndItsEventTogether()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        DescribeMenuItemOutcome outcome = await Administration().DescribeMenuItemAsync(
            identifier, "Lentil, vegan", _administratorIdentifier, cancellationToken);

        Assert.Equal(DescribeMenuItemOutcome.Described, outcome);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.Equal("Lentil, vegan", stored!.Description);

        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("description_changed", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            "Lentil, vegan",
            await ScalarAsync<string>("new_description", identifier, cancellationToken));

        Assert.Null(await ScalarAsync<string>("new_name", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<decimal?>("new_price_amount", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<int?>("new_display_order", identifier, cancellationToken));
    }

    [Fact]
    public async Task ClearingADescriptionIsAChangeAndStoresTheEmptyString()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Soup", "Lentil", 4.50m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            DescribeMenuItemOutcome.Described,
            await Administration().DescribeMenuItemAsync(
                identifier, null, _administratorIdentifier, cancellationToken));

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.Equal(string.Empty, stored!.Description);

        Assert.Equal(4, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("description_changed", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            string.Empty,
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
    }

    [Fact]
    public async Task DescribingWithTheSameTextIsANoOp_ButRecasingIsNot()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, "Soup", "Lentil", 4.50m, _administratorIdentifier, cancellationToken);

        int eventsAfterCreate = await World().CountAsync(CountEventsSql, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            DescribeMenuItemOutcome.NoChange,
            await Administration().DescribeMenuItemAsync(
                identifier, "  Lentil  ", _administratorIdentifier, cancellationToken));

        Assert.Equal(eventsAfterCreate, await World().CountAsync(CountEventsSql, cancellationToken));

        Assert.Equal(
            DescribeMenuItemOutcome.Described,
            await Administration().DescribeMenuItemAsync(
                identifier, "LENTIL", _administratorIdentifier, cancellationToken));

        Assert.Equal(eventsAfterCreate + 1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task ReorderWritesThePositionAndItsEvent_AndAMoveToTheSamePlaceIsANoOp()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            ReorderMenuItemOutcome.Reordered,
            await Administration().ReorderMenuItemAsync(
                identifier, 7, _administratorIdentifier, cancellationToken));

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.Equal(7, stored!.DisplayOrder);

        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("reordered", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(7, await ScalarAsync<int>("new_display_order", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_description", identifier, cancellationToken));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            ReorderMenuItemOutcome.NoChange,
            await Administration().ReorderMenuItemAsync(
                identifier, 7, _administratorIdentifier, cancellationToken));

        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task MovingAnItemToAnotherSectionAppendsItThereAndLogsBothEvents()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        await Administration().CreateMenuItemAsync(
            _identifiers.Create(), puddings, "Pie", description: null, 5.00m,
            _administratorIdentifier, cancellationToken);

        Assert.Equal(4, await World().CountAsync(CountEventsSql, cancellationToken));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            MoveMenuItemToSectionOutcome.Moved,
            await Administration().MoveMenuItemToSectionAsync(
                soup, puddings, _administratorIdentifier, cancellationToken));

        MenuItemSummary? stored = await Directory().GetAsync(soup, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(puddings, stored.MenuSectionIdentifier);
        Assert.Equal("Puddings", stored.MenuSectionName);

        Assert.Equal(1, stored.DisplayOrder);

        Assert.Equal("Soup", stored.Name);
        Assert.Equal(4.50m, stored.PriceAmount);
        Assert.True(stored.IsActive);

        Assert.Equal(6, await World().CountAsync(CountEventsSql, cancellationToken));

        Assert.Equal("reordered", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal(1, await ScalarAsync<int>("new_display_order", soup, cancellationToken));
    }

    [Fact]
    public async Task AMoveThatLandsOnTheSamePositionWritesNoReorderedEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);
        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        Assert.Equal(0, (await Directory().GetAsync(soup, cancellationToken))!.DisplayOrder);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            MoveMenuItemToSectionOutcome.Moved,
            await Administration().MoveMenuItemToSectionAsync(
                soup, puddings, _administratorIdentifier, cancellationToken));

        MenuItemSummary? stored = await Directory().GetAsync(soup, cancellationToken);
        Assert.Equal(puddings, stored!.MenuSectionIdentifier);
        Assert.Equal(0, stored.DisplayOrder);

        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("section_changed", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal(puddings, await ScalarAsync<Guid>("new_menu_section_identifier", soup, cancellationToken));
    }

    [Fact]
    public async Task MovingToTheSectionItIsAlreadyUnderWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            MoveMenuItemToSectionOutcome.NoChange,
            await Administration().MoveMenuItemToSectionAsync(
                soup, _sectionIdentifier, _administratorIdentifier, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(_sectionIdentifier, (await Directory().GetAsync(soup, cancellationToken))!.MenuSectionIdentifier);
    }

    [Fact]
    public async Task MovingToASectionThatDoesNotExistWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        Assert.Equal(
            MoveMenuItemToSectionOutcome.MenuSectionNotFound,
            await Administration().MoveMenuItemToSectionAsync(
                soup, _identifiers.Create(), _administratorIdentifier, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));

        MenuItemSummary? stored = await Directory().GetAsync(soup, cancellationToken);
        Assert.Equal(_sectionIdentifier, stored!.MenuSectionIdentifier);
        Assert.Equal(0, stored.DisplayOrder);
    }

    [Fact]
    public async Task MovingAnUnknownItemReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            MoveMenuItemToSectionOutcome.MenuItemNotFound,
            await Administration().MoveMenuItemToSectionAsync(
                _identifiers.Create(), _sectionIdentifier, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountItemsSql, cancellationToken));
    }

    [Fact]
    public async Task DescribeAndReorderRefuseAnUnknownItemWithoutWriting()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid unknown = _identifiers.Create();

        Assert.Equal(
            DescribeMenuItemOutcome.MenuItemNotFound,
            await Administration().DescribeMenuItemAsync(
                unknown, "Anything", _administratorIdentifier, cancellationToken));

        Assert.Equal(
            ReorderMenuItemOutcome.MenuItemNotFound,
            await Administration().ReorderMenuItemAsync(
                unknown, 3, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountItemsSql, cancellationToken));
    }

    [Fact]
    public async Task ANegativePositionIsRefusedBeforeAnythingIsWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = await CreateAsync("Soup", 4.50m, cancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await Administration().ReorderMenuItemAsync(
                identifier, -1, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, (await Directory().GetAsync(identifier, cancellationToken))!.DisplayOrder);

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    private async Task<Guid> CreateAsync(string name, decimal price, CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, name, description: null, price, _administratorIdentifier, cancellationToken);

        return identifier;
    }

    private async Task<string?> EventTypeAsync(Guid menuItemIdentifier, CancellationToken cancellationToken)
        => await ScalarAsync<string>("event_type", menuItemIdentifier, cancellationToken);

    private async Task<T?> ScalarAsync<T>(
        string column,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await World().ScalarAsync<T>(
            $"""
            SELECT {column}
            FROM menu_item_event
            WHERE menu_item_identifier = @MenuItemIdentifier
            ORDER BY occurred_at DESC, menu_item_event_identifier DESC
            LIMIT 1;
            """,
            new { MenuItemIdentifier = menuItemIdentifier },
            cancellationToken);

    private async Task<T?> CreatedEventScalarAsync<T>(
        string column,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await World().ScalarAsync<T>(
            $"""
            SELECT {column}
            FROM menu_item_event
            WHERE menu_item_identifier = @MenuItemIdentifier
              AND event_type = 'created';
            """,
            new { MenuItemIdentifier = menuItemIdentifier },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuAvailability Availability() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
