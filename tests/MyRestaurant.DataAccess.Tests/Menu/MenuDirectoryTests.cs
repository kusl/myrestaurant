using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuDirectoryTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 4, 2, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    public MenuDirectoryTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        await _world.TruncateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task List_ReturnsDeactivatedItemsToo_OrderedByName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await _world!.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        Guid salmon = await _world.AddMenuItemAsync("Salmon", 18.00m, cancellationToken, isActive: false);
        Guid apple = await _world.AddMenuItemAsync("Apple pie", 3.25m, cancellationToken);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            new[] { apple, salmon, soup },
            menu.Select(item => item.MenuItemIdentifier).ToArray());

        Assert.Equal(new[] { "Apple pie", "Salmon", "Soup" }, menu.Select(item => item.Name).ToArray());
        Assert.Equal(new[] { true, false, true }, menu.Select(item => item.IsActive).ToArray());
        Assert.Equal(18.00m, menu.Single(item => item.MenuItemIdentifier == salmon).PriceAmount);
    }

    [Fact]
    public async Task Get_ReadsOneItem_AndAnswersNullForAnUnknownIdentifier()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tea = await _world!.AddMenuItemAsync("Tea", 2.00m, cancellationToken);

        MenuItemSummary? found = await Directory().GetAsync(tea, cancellationToken);

        Assert.NotNull(found);
        Assert.Equal("Tea", found!.Name);
        Assert.Equal(2.00m, found.PriceAmount);
        Assert.True(found.IsActive);
        Assert.Equal(_clock.UtcNow, found.CreatedAt);

        Assert.Equal(string.Empty, found.Description);
        Assert.Equal(0, found.DisplayOrder);

        Assert.Equal("Menu", found.MenuSectionName);
        Assert.True(found.MenuSectionIsActive);
        Assert.NotEqual(Guid.Empty, found.MenuSectionIdentifier);

        Assert.Null(await Directory().GetAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task List_OrdersByPositionBeforeName_AndCarriesTheDescription()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid zucchini = await _world!.AddMenuItemAsync(
            "Zucchini fries", 5.00m, cancellationToken, description: "With aioli", displayOrder: 0);
        Guid apple = await _world.AddMenuItemAsync(
            "Apple pie", 3.25m, cancellationToken, displayOrder: 2);
        Guid mousse = await _world.AddMenuItemAsync(
            "Mousse", 4.00m, cancellationToken, description: "Dark chocolate", displayOrder: 1);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            new[] { zucchini, mousse, apple },
            menu.Select(item => item.MenuItemIdentifier).ToArray());

        Assert.Equal(new[] { 0, 1, 2 }, menu.Select(item => item.DisplayOrder).ToArray());

        Assert.Equal(
            new[] { "With aioli", "Dark chocolate", "" },
            menu.Select(item => item.Description).ToArray());
    }

    [Fact]
    public async Task TwoItemsAtTheSamePosition_AreOrderedByName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid zebra = await _world!.AddMenuItemAsync("Zebra cake", 6.00m, cancellationToken, displayOrder: 4);
        Guid apple = await _world.AddMenuItemAsync("Apple tart", 6.00m, cancellationToken, displayOrder: 4);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            new[] { apple, zebra },
            menu.Select(item => item.MenuItemIdentifier).ToArray());
    }

    [Fact]
    public async Task List_OrdersBySectionBeforeItem_AndCarriesTheSectionName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid puddings = await _world!.AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);
        Guid starters = await _world.AddMenuSectionAsync("Starters", cancellationToken, displayOrder: 0);

        Guid soup = await _world.AddMenuItemAsync(
            "Soup", 4.50m, cancellationToken, displayOrder: 0, menuSectionIdentifier: starters);
        Guid bread = await _world.AddMenuItemAsync(
            "Bread", 2.00m, cancellationToken, displayOrder: 1, menuSectionIdentifier: starters);
        Guid trifle = await _world.AddMenuItemAsync(
            "Trifle", 5.00m, cancellationToken, displayOrder: 0, menuSectionIdentifier: puddings);
        Guid apple = await _world.AddMenuItemAsync(
            "Apple pie", 3.25m, cancellationToken, displayOrder: 1, menuSectionIdentifier: puddings);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            new[] { soup, bread, trifle, apple },
            menu.Select(item => item.MenuItemIdentifier).ToArray());

        Assert.Equal(
            new[] { "Starters", "Starters", "Puddings", "Puddings" },
            menu.Select(item => item.MenuSectionName).ToArray());

        Assert.Equal(
            2,
            menu.Select(item => item.MenuSectionIdentifier)
                .Where((identifier, index) => index == 0 || identifier != menu[index - 1].MenuSectionIdentifier)
                .Count());
    }

    [Fact]
    public async Task AnInactiveSection_IsCarriedRatherThanFiltered()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid breakfast = await _world!.AddMenuSectionAsync(
            "Breakfast", cancellationToken, isActive: false);

        Guid eggs = await _world.AddMenuItemAsync(
            "Eggs", 5.00m, cancellationToken, menuSectionIdentifier: breakfast);

        MenuItemSummary listed = Assert.Single(await Directory().ListAsync(cancellationToken));

        Assert.Equal(eggs, listed.MenuItemIdentifier);
        Assert.False(listed.MenuSectionIsActive);

        Assert.True(listed.IsActive);
    }

    [Fact]
    public async Task List_CarriesEachHeadingsOwnDescription_OnEveryItemUnderIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string startersDescription = "Served until 11am.";

        Guid starters = await _world!.AddMenuSectionAsync(
            "Starters", cancellationToken, description: startersDescription, displayOrder: 0);
        Guid puddings = await _world.AddMenuSectionAsync(
            "Puddings", cancellationToken, displayOrder: 1);

        await _world.AddMenuItemAsync(
            "Soup", 4.50m, cancellationToken, displayOrder: 0, menuSectionIdentifier: starters);
        await _world.AddMenuItemAsync(
            "Bread", 2.00m, cancellationToken, displayOrder: 1, menuSectionIdentifier: starters);
        await _world.AddMenuItemAsync(
            "Trifle", 5.00m, cancellationToken, displayOrder: 0, menuSectionIdentifier: puddings);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            new[] { startersDescription, startersDescription, string.Empty },
            menu.Select(item => item.MenuSectionDescription).ToArray());

        Assert.Equal(
            new[] { "Starters", "Starters", "Puddings" },
            menu.Select(item => item.MenuSectionName).ToArray());
    }

    [Fact]
    public async Task AnEmptyMenu_IsAnEmptyList_NotAFailure()
    {
        SkipIfNoContainer();

        Assert.Empty(await Directory().ListAsync(TestContext.Current.CancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuDirectory Directory() => new(_connectionFactory!);
}
