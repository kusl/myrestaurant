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
/// stores a number and has no opinion about what it means. <c>0005</c> adds two more, and the reason is
/// the same one register up: the reader now sorts by section before it sorts by position, and that key
/// exists nowhere else.</para>
///
/// <para><b>Most facts here still say nothing about sections, and that is by construction.</b>
/// <c>OrderTestWorld.AddMenuItemAsync</c> files an item under a lazily created house section when the
/// caller does not name one, so "an item exists" stayed a one-line arrangement through a migration that
/// made its heading mandatory. A test that is about headings passes one.</para>
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

        // 0005's join, seen from the reader. The name comes from menu_section rather than from anything
        // stored on the item, which is what makes a rename take effect everywhere at once — and the
        // INNER join is what a missing section would turn into a missing row, so a null here would be a
        // reader that had gone back to reading menu_item alone.
        Assert.Equal("Menu", found.MenuSectionName);
        Assert.True(found.MenuSectionIsActive);
        Assert.NotEqual(Guid.Empty, found.MenuSectionIdentifier);

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

    /// <summary>
    /// <c>0005</c>'s ordering, and the one assertion that separates a reader which sorts by section from
    /// one that merely selects the column.
    ///
    /// <para>Every trap is set on purpose. The <em>second</em> section is created first and given the
    /// higher position, so section creation order and section display order disagree. Its name sorts
    /// <em>before</em> the other's, so a reader that ordered by section name would also fail. And the
    /// item positions are the reverse of alphabetical within each heading, so a reader that grouped
    /// correctly and then sorted items by name would fail too. A reader with <c>ORDER BY (display_order,
    /// name, identifier)</c> — exactly what <c>0004</c> left behind — returns these four in a different
    /// order than any of the above.</para>
    /// </summary>
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

        // Contiguity is the property §11.1's surface actually depends on: it groups by walking the list
        // once and starting a heading when the identifier changes, so a correct set of names in a
        // scattered order would render the same heading twice.
        Assert.Equal(
            2,
            menu.Select(item => item.MenuSectionIdentifier)
                .Where((identifier, index) => index == 0 || identifier != menu[index - 1].MenuSectionIdentifier)
                .Count());
    }

    /// <summary>
    /// An inactive section is <em>carried</em>, not filtered — the same rule
    /// <see cref="List_ReturnsDeactivatedItemsToo_OrderedByName"/> asserts for an item, and for the
    /// opposite downstream reason.
    ///
    /// <para>§7 hides an inactive heading from the <b>guest</b> and shows it to §11.4's administrator, so
    /// the filtering belongs to the surface and this reader must hand both of them everything. A
    /// directory that filtered here would leave the administration index unable to show an item whose
    /// heading is switched off — which is precisely the row somebody is looking for when they wonder why
    /// an available dish is not on the menu.</para>
    /// </summary>
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

        // And the item's own flag is untouched. §7 forbids the cascade: switching off a heading must not
        // rewrite which dishes the kitchen had 86'd, because reactivating it has to bring the menu back
        // exactly as it was.
        Assert.True(listed.IsActive);
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
