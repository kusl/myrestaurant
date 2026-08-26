using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuItemResequenceTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountReorderedEventsSql = """
        SELECT count(*)::int FROM menu_item_event WHERE event_type = 'reordered';
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 19, 11, 20, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    public MenuItemResequenceTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _administratorIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResequencingAssignsPositionsFromThePlaceInTheList()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid heading = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(heading, cancellationToken);

        Assert.Equal(
            ResequenceMenuItemsOutcome.Resequenced,
            await Administration().ResequenceMenuItemsAsync(
                heading, [cola, tea, coffee], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuItemSummary> stored = await ItemsUnderAsync(heading, cancellationToken);

        Assert.Equal([cola, tea, coffee], stored.Select(summary => summary.MenuItemIdentifier));
        Assert.Equal([0, 1, 2], stored.Select(summary => summary.DisplayOrder));
    }

    [Fact]
    public async Task OnlyTheItemsThatMovedGetAnEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid heading = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(heading, cancellationToken);

        Assert.Equal(
            ResequenceMenuItemsOutcome.Resequenced,
            await Administration().ResequenceMenuItemsAsync(
                heading, [cola, coffee, tea], _administratorIdentifier, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountReorderedEventsSql, cancellationToken));

        Assert.Equal([2], await ReorderedPositionsAsync(tea, cancellationToken));
        Assert.Empty(await ReorderedPositionsAsync(coffee, cancellationToken));
        Assert.Equal([0], await ReorderedPositionsAsync(cola, cancellationToken));
    }

    [Fact]
    public async Task ResequencingIntoTheStoredOrderWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid heading = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(heading, cancellationToken);

        Assert.Equal(
            ResequenceMenuItemsOutcome.NoChange,
            await Administration().ResequenceMenuItemsAsync(
                heading, [tea, coffee, cola], _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AListThatIsNotAPermutationIsRefusedWhole()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);

        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(drinks, cancellationToken);
        Guid trifle = await World().AddMenuItemAsync(
            "Trifle", 6.00m, cancellationToken, menuSectionIdentifier: puddings);

        foreach (Guid[] wrong in new[]
        {
            new[] { cola, tea },
            [cola, tea, tea],
            [cola, tea, trifle],
        })
        {
            Assert.Equal(
                ResequenceMenuItemsOutcome.MenuItemSetChanged,
                await Administration().ResequenceMenuItemsAsync(
                    drinks, wrong, _administratorIdentifier, cancellationToken));
        }

        IReadOnlyList<MenuItemSummary> stored = await ItemsUnderAsync(drinks, cancellationToken);

        Assert.Equal([tea, coffee, cola], stored.Select(summary => summary.MenuItemIdentifier));
        Assert.Equal(0, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AnUnknownHeadingIsRefusedAsASetThatChanged()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        (Guid tea, Guid coffee, _) = await ThreeItemsAsync(drinks, cancellationToken);

        Assert.Equal(
            ResequenceMenuItemsOutcome.MenuItemSetChanged,
            await Administration().ResequenceMenuItemsAsync(
                _identifiers.Create(), [coffee, tea], _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
    }

    [Fact]
    public async Task ResequencingOneHeadingLeavesTheOtherHeadingAlone()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);

        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(drinks, cancellationToken);

        Guid trifle = await World().AddMenuItemAsync(
            "Trifle", 6.00m, cancellationToken, displayOrder: 0, menuSectionIdentifier: puddings);
        Guid sorbet = await World().AddMenuItemAsync(
            "Sorbet", 4.00m, cancellationToken, displayOrder: 1, menuSectionIdentifier: puddings);

        Assert.Equal(
            ResequenceMenuItemsOutcome.Resequenced,
            await Administration().ResequenceMenuItemsAsync(
                drinks, [cola, tea, coffee], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuItemSummary> untouched = await ItemsUnderAsync(puddings, cancellationToken);

        Assert.Equal([trifle, sorbet], untouched.Select(summary => summary.MenuItemIdentifier));
        Assert.Equal([0, 1], untouched.Select(summary => summary.DisplayOrder));

        Assert.Equal(3, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
        Assert.Empty(await ReorderedPositionsAsync(trifle, cancellationToken));
        Assert.Empty(await ReorderedPositionsAsync(sorbet, cancellationToken));
    }

    [Fact]
    public async Task ResequencingSeparatesTwoItemsThatSharedAPosition()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid heading = await World().AddMenuSectionAsync("Drinks", cancellationToken);

        Guid zebra = await World().AddMenuItemAsync(
            "Zebra water", 3.00m, cancellationToken, displayOrder: 0, menuSectionIdentifier: heading);
        Guid apple = await World().AddMenuItemAsync(
            "Apple juice", 3.50m, cancellationToken, displayOrder: 0, menuSectionIdentifier: heading);

        IReadOnlyList<MenuItemSummary> tied = await ItemsUnderAsync(heading, cancellationToken);
        Assert.Equal([apple, zebra], tied.Select(summary => summary.MenuItemIdentifier));
        Assert.Equal([0, 0], tied.Select(summary => summary.DisplayOrder));

        Assert.Equal(
            ResequenceMenuItemsOutcome.Resequenced,
            await Administration().ResequenceMenuItemsAsync(
                heading, [zebra, apple], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuItemSummary> ordered = await ItemsUnderAsync(heading, cancellationToken);
        Assert.Equal([zebra, apple], ordered.Select(summary => summary.MenuItemIdentifier));
        Assert.Equal([0, 1], ordered.Select(summary => summary.DisplayOrder));
    }

    [Fact]
    public async Task TheEventsOfOneResequenceReadInTheOrderTheRowsWereWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid heading = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(heading, cancellationToken);

        DateTimeOffset moment = _clock.UtcNow.AddMinutes(5);
        _clock.UtcNow = moment;

        Assert.Equal(
            ResequenceMenuItemsOutcome.Resequenced,
            await Administration().ResequenceMenuItemsAsync(
                heading, [cola, tea, coffee], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuItemEventEntry> events = await EventLog()
            .ListForItemAsync(cola, cancellationToken);

        MenuItemEventEntry moved = Assert.Single(
            events,
            entry => string.Equals(entry.EventType, "reordered", StringComparison.Ordinal));

        Assert.Equal(moment, moved.OccurredAt);
        Assert.Equal(_administratorIdentifier, moved.ActorPersonIdentifier);

        IReadOnlyList<Guid> written = await ReorderedItemsInReadOrderAsync(moment, cancellationToken);

        Assert.Equal([cola, tea, coffee], written);
    }

    private async Task<(Guid Tea, Guid Coffee, Guid Cola)> ThreeItemsAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
        => (await World().AddMenuItemAsync(
                "Tea", 2.00m, cancellationToken, displayOrder: 0, menuSectionIdentifier: menuSectionIdentifier),
            await World().AddMenuItemAsync(
                "Coffee", 2.50m, cancellationToken, displayOrder: 1, menuSectionIdentifier: menuSectionIdentifier),
            await World().AddMenuItemAsync(
                "Cola", 2.75m, cancellationToken, displayOrder: 2, menuSectionIdentifier: menuSectionIdentifier));

    private async Task<IReadOnlyList<MenuItemSummary>> ItemsUnderAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MenuItemSummary> all = await Directory().ListAsync(cancellationToken);

        return [.. all.Where(summary => summary.MenuSectionIdentifier == menuSectionIdentifier)];
    }

    private async Task<IReadOnlyList<int>> ReorderedPositionsAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MenuItemEventEntry> events = await EventLog()
            .ListForItemAsync(menuItemIdentifier, cancellationToken);

        return
        [
            .. events
                .Where(entry => string.Equals(entry.EventType, "reordered", StringComparison.Ordinal))
                .Select(entry => entry.NewDisplayOrder ?? -1),
        ];
    }

    private async Task<IReadOnlyList<Guid>> ReorderedItemsInReadOrderAsync(
        DateTimeOffset moment,
        CancellationToken cancellationToken)
        => await World().QueryAsync<Guid>(
            """
            SELECT menu_item_identifier
            FROM menu_item_event
            WHERE event_type = 'reordered' AND occurred_at = @Moment
            ORDER BY occurred_at, menu_item_event_identifier;
            """,
            new { Moment = moment },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuDirectory Directory() => new(_connectionFactory!);

    private DapperMenuEventLog EventLog() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
