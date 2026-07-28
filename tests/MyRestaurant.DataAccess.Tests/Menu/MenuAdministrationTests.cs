using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuAdministration"/> against a real PostgreSQL 17 container —
/// §7's create, rename, and reprice, behind §11.4's menu section.
///
/// <para>The facts worth pinning are about the <em>pair</em> of rows, not the column. Every one of these
/// three verbs writes a <c>menu_item</c> change and a mirroring <c>menu_item_event</c> in one
/// transaction, and §8.2's paired CHECKs make each event type carry exactly one shape of payload —
/// <c>created</c> both the name and the price, <c>name_changed</c> the name alone,
/// <c>price_changed</c> the price alone. Get that wrong and the database refuses the write, which is the
/// good failure; get the <em>rounding</em> wrong and the row and its event silently disagree by two
/// hundredths, which is not, so <see cref="CreateRoundsToTheStoredScaleInBothRows"/> and
/// <see cref="RepricingToTheSameNumberAtADifferentScaleIsANoOp"/> exist.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
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
            identifier, "Salmon", 18.00m, _administratorIdentifier, cancellationToken);

        Assert.Equal(identifier, result.MenuItemIdentifier);
        Assert.Equal("Salmon", result.Name);
        Assert.Equal(18.00m, result.PriceAmount);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Salmon", stored.Name);
        Assert.Equal(18.00m, stored.PriceAmount);

        // §7 creates active: an item nobody can order is not a menu item yet.
        Assert.True(stored.IsActive);
        Assert.Equal(_clock.UtcNow, stored.CreatedAt);

        // §8.2's CHECK requires BOTH payload columns for 'created'.
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("created", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal("Salmon", await ScalarAsync<string>("new_name", identifier, cancellationToken));
        Assert.Equal(18.00m, await ScalarAsync<decimal>("new_price_amount", identifier, cancellationToken));
    }

    [Fact]
    public async Task TheCreatedEventRecordsTheActorAndTheInstant()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, "Salmon", 18.00m, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            _administratorIdentifier,
            await ScalarAsync<Guid>("actor_person_identifier", identifier, cancellationToken));

        DateTime occurredAt = await ScalarAsync<DateTime>("occurred_at", identifier, cancellationToken);
        Assert.Equal(_clock.UtcNow.UtcDateTime, DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc));
    }

    /// <summary>
    /// <c>price_amount</c> and <c>new_price_amount</c> are both <c>numeric(10,2)</c>, so PostgreSQL would
    /// round a third decimal on its own — quietly, and separately for each row. Rounding once before
    /// either write is what guarantees the row, its event, and the value handed back to the caller are the
    /// same number.
    /// </summary>
    [Fact]
    public async Task CreateRoundsToTheStoredScaleInBothRows()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, "Soup", 4.567m, _administratorIdentifier, cancellationToken);

        Assert.Equal(4.57m, result.PriceAmount);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(4.57m, stored.PriceAmount);
        Assert.Equal(4.57m, await ScalarAsync<decimal>("new_price_amount", identifier, cancellationToken));
    }

    [Fact]
    public async Task CreateTrimsTheName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuItemResult result = await Administration().CreateMenuItemAsync(
            identifier, "  Soup  ", 4.50m, _administratorIdentifier, cancellationToken);

        Assert.Equal("Soup", result.Name);
        Assert.Equal("Soup", await ScalarAsync<string>("new_name", identifier, cancellationToken));
    }

    /// <summary>
    /// <c>menu_item.name</c> carries no UNIQUE constraint (§8.2), unlike <c>restaurant_table.label</c>.
    /// A rotating special really is called the same thing twice, and this layer does not get to invent a
    /// constraint the schema of record does not have.
    /// </summary>
    [Fact]
    public async Task TwoItemsMayShareAName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Administration().CreateMenuItemAsync(
            _identifiers.Create(), "Soup", 4.50m, _administratorIdentifier, cancellationToken);
        await Administration().CreateMenuItemAsync(
            _identifiers.Create(), "Soup", 5.50m, _administratorIdentifier, cancellationToken);

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

        // The price is untouched, and §8.2's CHECK requires new_price_amount to be NULL for this type.
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

    /// <summary>
    /// <c>name</c> is <c>text</c>, not <c>citext</c> — unlike <c>person.username</c>. Changing the case is
    /// a change somebody meant to make, and the log has to admit it happened.
    /// </summary>
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

        // The name is untouched, and §8.2's CHECK requires new_name to be NULL for this type.
        Assert.Equal("Soup", stored.Name);
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("price_changed", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal(5.25m, await ScalarAsync<decimal>("new_price_amount", soup, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_name", soup, cancellationToken));
    }

    /// <summary>
    /// 4.500 and 4.50 are the same price. Rounding before comparing is what stops a form that helpfully
    /// posts a third decimal from writing an event that records nothing having changed.
    /// </summary>
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

    /// <summary>
    /// A negative price is a <c>numeric(10,2) CHECK (price_amount &gt;= 0)</c> violation and an
    /// eight-digit overflow is error 22003; both would surface as an opaque exception well after the form
    /// that caused them. Refusing before the connection is opened names the problem instead — and no
    /// connection is opened, so this test needs no container to be meaningful.
    /// </summary>
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

    /// <summary>
    /// The whole point of ADR-0002 for this table: four changes leave four rows, not one overwritten
    /// column and a shrug. The 86 comes through <see cref="DapperMenuAvailability"/>, which is a different
    /// class writing the same log — this asserts the two agree about what a log is.
    /// </summary>
    [Fact]
    public async Task TheHistoryKeepsEveryChangeFromBothWriteServices()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, "Soup", 4.50m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().RenameMenuItemAsync(
            identifier, "Soup of the day", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().RepriceMenuItemAsync(
            identifier, 5.00m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Availability().SetActiveAsync(
            identifier, isActive: false, _administratorIdentifier, cancellationToken);

        Assert.Equal(4, await World().CountAsync(CountEventsSql, cancellationToken));

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

    private async Task<string?> EventTypeAsync(Guid menuItemIdentifier, CancellationToken cancellationToken)
        => await ScalarAsync<string>("event_type", menuItemIdentifier, cancellationToken);

    /// <summary>
    /// The newest event for one item. Every test here writes at most one event per item per instant, so
    /// "newest" is unambiguous; the identifier tiebreak matches the reader's ordering.
    /// </summary>
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

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuAvailability Availability() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
