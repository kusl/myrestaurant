using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuAdministration.ResequenceMenuItemsAsync"/> against a real
/// PostgreSQL 17 container — §7's whole-list reordering for the items under one heading, which is the write
/// behind the Up and Down controls on each item row of <c>/administration/menu</c>.
///
/// <para><b>Its own class rather than more facts on <see cref="MenuAdministrationTests"/>, on the reasoning
/// <see cref="MenuSectionResequenceTests"/> records one register up.</b> Every other verb in that file
/// writes one row and one event, so <em>the newest event for this item</em> is unambiguous and every helper
/// it owns is built on that. This verb writes several rows and several events in one transaction at one
/// instant, so the facts worth pinning are about <em>sets</em> and <em>sequences</em>.</para>
///
/// <para><b>Two of these facts have no counterpart one register up, and they are the reason this is a
/// separate slice rather than a widening of Slice 47.</b> The section verb's set is the whole table; this
/// verb's set is <em>one heading's</em> items, so it must be shown that a resequence under one heading
/// leaves every other heading's positions and events completely untouched
/// (<see cref="ResequencingOneHeadingLeavesTheOtherHeadingAlone"/>), and that an unknown heading is refused
/// through the ordinary permutation comparison rather than through a fourth outcome
/// (<see cref="AnUnknownHeadingIsRefusedAsASetThatChanged"/>). The first is the one to read: an off-by-one
/// in the WHERE clause would renumber the puddings because somebody moved a drink, and every assertion
/// about the drinks would still pass.</para>
///
/// <para><b>Three facts are about what is NOT written.</b> A resequence that moves one item in four writes
/// two rows, not four; a resequence into the order already stored writes nothing at all; and a list that is
/// not a permutation of that heading's items is refused whole rather than partially obeyed. Each of those
/// fails silently and leaves an order nobody chose, which is the worse of the two failures in an append-only
/// system (ADR-0002).</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
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

    /// <summary>
    /// The ordering is stored as the list's indices, and the read returns the list.
    /// </summary>
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

    /// <summary>
    /// One event per item that actually moved. Reversing three items leaves the middle one where it was, so
    /// this writes two <c>reordered</c> events and not three — the no-op rule applied per row rather than
    /// per call.
    /// </summary>
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

    /// <summary>
    /// A resequence into the order already stored writes nothing at all and says so.
    /// </summary>
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

    /// <summary>
    /// The three shapes a list can be wrong in, and one answer to all of them: short, repeating an
    /// identifier, and naming an item filed under another heading. The repeated-identifier case is the one a
    /// length check and a membership check each admit on their own, which is why the permutation test
    /// de-duplicates before it resolves.
    /// </summary>
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

    /// <summary>
    /// <b>A heading this menu does not hold is refused through the same comparison as everything else</b>,
    /// which is why there is no fourth outcome for it: an unknown heading has no items under it, so any
    /// non-empty list against it fails the permutation test. Recorded as a fact rather than left implicit,
    /// because "no rows came back" is also what an empty heading looks like, and the two agreeing is a
    /// decision rather than an accident.
    /// </summary>
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

    /// <summary>
    /// <b>The fact this verb has and the section verb cannot have.</b> A position is a position within a
    /// heading, so resequencing one heading must leave every other heading's rows and events exactly as they
    /// were — a WHERE clause that reached one row too far would renumber a list nobody touched, and every
    /// assertion about the heading that <em>was</em> touched would still pass.
    /// </summary>
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

        // Two events, both under Drinks: the puddings wrote none at all.
        Assert.Equal(2, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
        Assert.Empty(await ReorderedPositionsAsync(trifle, cancellationToken));
        Assert.Empty(await ReorderedPositionsAsync(sorbet, cancellationToken));
    }

    /// <summary>
    /// Positions are permitted to be equal and are not required to be contiguous, which is the whole reason
    /// this verb exists rather than an absolute write per item. Two dishes sharing position 0 have an order
    /// nobody assigned — the name tie-break decides it — and a resequence gives them one.
    /// </summary>
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

    /// <summary>
    /// Every event of one call carries the same instant and the acting administrator, and they read back in
    /// the order the rows were written rather than in an order the random bits chose (<b>F-95</b>). Asserted
    /// through the log reader rather than against raw identifiers, because what matters is what §11.4
    /// renders.
    /// </summary>
    [Fact]
    public async Task TheEventsOfOneResequenceReadInTheOrderTheRowsWereWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid heading = await World().AddMenuSectionAsync("Drinks", cancellationToken);
        (Guid tea, Guid coffee, Guid cola) = await ThreeItemsAsync(heading, cancellationToken);

        DateTimeOffset moment = _clock.UtcNow.AddMinutes(5);
        _clock.UtcNow = moment;

        // Rotates all three, so all three move and the write order is cola, tea, coffee.
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

    /// <summary>
    /// Three items under one heading at 0, 1, 2 in the order they are named.
    /// </summary>
    private async Task<(Guid Tea, Guid Coffee, Guid Cola)> ThreeItemsAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
        => (await World().AddMenuItemAsync(
                "Tea", 2.00m, cancellationToken, displayOrder: 0, menuSectionIdentifier: menuSectionIdentifier),
            await World().AddMenuItemAsync(
                "Coffee", 2.50m, cancellationToken, displayOrder: 1, menuSectionIdentifier: menuSectionIdentifier),
            await World().AddMenuItemAsync(
                "Cola", 2.75m, cancellationToken, displayOrder: 2, menuSectionIdentifier: menuSectionIdentifier));

    /// <summary>
    /// One heading's items in the order §7's six-key read returns them — filtered from the directory rather
    /// than queried per heading, which is exactly what <c>AdministrationMenu.razor</c> does and therefore
    /// the order the surface's Up and Down exchange entries in.
    /// </summary>
    private async Task<IReadOnlyList<MenuItemSummary>> ItemsUnderAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MenuItemSummary> all = await Directory().ListAsync(cancellationToken);

        return [.. all.Where(summary => summary.MenuSectionIdentifier == menuSectionIdentifier)];
    }

    /// <summary>
    /// The positions one item's <c>reordered</c> events recorded, oldest first — read through the log reader
    /// so the assertion is about what §11.4 renders.
    /// </summary>
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

    /// <summary>
    /// Which items were reordered at one instant, in the order <c>(occurred_at,
    /// menu_item_event_identifier)</c> puts them — the ordering every §11.4 history reads under.
    /// </summary>
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
