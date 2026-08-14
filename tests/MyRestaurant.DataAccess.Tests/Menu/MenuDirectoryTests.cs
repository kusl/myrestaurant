using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuDirectory"/> (TECHNICAL_SPECIFICATION §7) against a real
/// PostgreSQL 17 container. Small, because the read side is small — but the first fact is the one that
/// matters: §7 requires a deactivated item to stay on the menu marked unavailable rather than vanish, so
/// a directory that filtered inactive items would quietly break the guest experience the specification
/// asks for, and nothing else would catch it.
///
/// <para><c>0004</c> adds two facts about the ordering, and they are here rather than in
/// <c>MenuAdministrationTests</c> because ordering is a property of the <em>read</em>: the write side
/// stores a number and has no opinion about what it means.</para>
/// </summary>
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

        // §7: "the guest sees that the salmon exists and is out, rather than watching it silently vanish".
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

        // 0004's column defaults, seen from the reader: an item arranged without either gets "" and 0,
        // never null and never "unset".
        Assert.Equal(string.Empty, found.Description);
        Assert.Equal(0, found.DisplayOrder);

        Assert.Null(await Directory().GetAsync(_identifiers.Create(), cancellationToken));
    }

    /// <summary>
    /// <c>0004</c>'s two columns, read back through the record. The interesting half is the ordering: the
    /// reader now sorts by <c>(display_order, name, identifier)</c>, and the fact worth pinning is that a
    /// position <em>overrides</em> the alphabet rather than decorating it — a reader that had kept
    /// <c>ORDER BY name</c> and merely selected the new column would pass every other assertion in this
    /// file.
    /// </summary>
    [Fact]
    public async Task List_OrdersByPositionBeforeName_AndCarriesTheDescription()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Deliberately the reverse of alphabetical order, so name ordering and position ordering
        // disagree on every pair.
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

        // "" rather than null for the one without a description: the column is NOT NULL DEFAULT '' so a
        // surface tests Length rather than for null (§8.2).
        Assert.Equal(
            new[] { "With aioli", "Dark chocolate", "" },
            menu.Select(item => item.Description).ToArray());
    }

    /// <summary>
    /// Two items at the same position render in a stable order, broken by name. The schema permits equal
    /// positions deliberately (§8.2 — a unique ordering column would make every move a two-phase
    /// rewrite), so this is the behaviour that makes that permission safe rather than arbitrary.
    ///
    /// <para>It is also the fact that makes <c>0004</c> invisible: <b>everything</b> in this project sits
    /// at position 0 until <c>0005</c>, so this tie-break is the whole of the ordering on every existing
    /// tree — which is why <see cref="List_ReturnsDeactivatedItemsToo_OrderedByName"/> still asserts the
    /// alphabet and still passes.</para>
    /// </summary>
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
    public async Task AnEmptyMenu_IsAnEmptyList_NotAFailure()
    {
        SkipIfNoContainer();

        Assert.Empty(await Directory().ListAsync(TestContext.Current.CancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuDirectory Directory() => new(_connectionFactory!);
}
