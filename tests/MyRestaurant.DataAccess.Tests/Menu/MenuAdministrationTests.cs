using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuAdministration"/> against a real PostgreSQL 17 container —
/// §7's create, rename, reprice, describe, reorder and move-to-section, behind §11.4's menu section.
///
/// <para><b>The move is the last verb of the menu enhancement to be written, and it is the only one here
/// that takes two locks.</b> Its facts are about where the item lands rather than that it landed: a
/// heading is appended to, so an item moved into a heading that already holds something must sit behind
/// it, and a move that changes the position must say so in a second event because §8.2 binds
/// <c>new_display_order</c> to <c>reordered</c> alone. An implementation that carried the old position
/// across would pass every other assertion in this file.</para>
///
/// <para>The facts worth pinning are about the <em>pair</em> of rows, not the column. Every one of these
/// verbs writes a <c>menu_item</c> change and a mirroring <c>menu_item_event</c> in one
/// transaction, and §8.2's named paired CHECKs make each event type carry exactly one shape of payload —
/// <c>created</c> both the name and the price, <c>name_changed</c> the name alone,
/// <c>price_changed</c> the price alone, <c>description_changed</c> the description alone,
/// <c>reordered</c> the position alone. Get that wrong and the database refuses the write, which is the
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

    /// <summary>
    /// The heading every create in this class files its item under (§7, <c>0005</c>). Made once per test
    /// in <see cref="InitializeAsync"/> rather than per fact, because an item's section is a mandatory
    /// argument and almost nothing here is <em>about</em> sections — the three facts that are make their
    /// own.
    /// </summary>
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

        // §7 creates active: an item nobody can order is not a menu item yet.
        Assert.True(stored.IsActive);
        Assert.Equal(_clock.UtcNow, stored.CreatedAt);

        // 0005's join, read back: the item is under the heading it was created in.
        Assert.Equal(_sectionIdentifier, stored.MenuSectionIdentifier);
        Assert.Equal("Mains", stored.MenuSectionName);
        Assert.Equal("Mains", result.MenuSectionName);

        // TWO events, and this is the count 0005 changed. §8.2 keeps 'created' at the name and the price,
        // so the heading is recorded by a 'section_changed' beside it — one transaction, two rows. The
        // read below is newest-first with the UUIDv7 tiebreak, so it is the section event.
        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("section_changed", await EventTypeAsync(identifier, cancellationToken));

        Guid? loggedSection = await ScalarAsync<Guid?>(
            "new_menu_section_identifier", identifier, cancellationToken);
        Assert.Equal(_sectionIdentifier, loggedSection);

        // And the 'created' event still carries exactly what §8.2's CHECK requires of it. Read by type
        // rather than by recency, because the newest event is now the section one — a fact that used to
        // be reachable through EventTypeAsync and no longer is.
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

    /// <summary>
    /// §7's heading is mandatory, and a section that does not exist is <em>reported</em> rather than
    /// raised. Without this the caller would meet PostgreSQL error 23503 — a foreign-key violation naming
    /// a constraint — which a surface cannot turn into a sentence about which field was wrong.
    ///
    /// <para>The other half is that nothing is written. The create takes the section row <c>FOR
    /// UPDATE</c> before it inserts anything, so a missing heading costs one failed lookup rather than a
    /// half-built item rolled back.</para>
    /// </summary>
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

    /// <summary>
    /// An item is appended to the end of its own section, which is <c>0005</c> reversing <c>0004</c>'s
    /// rule — and the reason the rule could change is that "the end of the menu" became a defined place
    /// the moment an item had a heading.
    ///
    /// <para><b>The second section is what makes this an assertion rather than a counter.</b> Positions
    /// are <em>within</em> a section, so a first item under a second heading must be at 0 again. An
    /// implementation that appended a menu-wide <c>MAX + 1</c> — the obvious wrong answer, and the one
    /// <c>0004</c> explicitly declined — would put it at 2 and pass every other fact in this file.</para>
    /// </summary>
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

        // Echoed from the result and read back from the row, because the two could disagree: the
        // position is assigned inside the transaction and returned without a second query.
        MenuItemSummary? storedSecond = await Directory().GetAsync(second.MenuItemIdentifier, cancellationToken);
        Assert.NotNull(storedSecond);
        Assert.Equal(1, storedSecond.DisplayOrder);
    }

    /// <summary>
    /// <c>MAX + 1</c> rather than <c>COUNT(*)</c>, which is the same number until something moves and then
    /// never is again. An item at position 5 in a section of one makes a count-based implementation hand
    /// out 1 — colliding with nothing here, but colliding with a real position on any menu somebody has
    /// reordered. The rule is stated in <c>DapperMenuSectionAdministration</c> and this is the item-side
    /// assertion of it.
    /// </summary>
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
            identifier, _sectionIdentifier, "Soup", description: null, 4.567m, _administratorIdentifier, cancellationToken);

        Assert.Equal(4.57m, result.PriceAmount);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(4.57m, stored.PriceAmount);

        // Read by TYPE rather than by recency. 0005 made the newest event of a create the
        // 'section_changed' one, whose price is null by CHECK — so the recency helper answers 0 here and
        // the failure reads as a rounding bug in a class whose whole subject is rounding.
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

        // Five, not four: the create contributes 'created' AND 'section_changed' as of 0005, and then a
        // rename, a reprice and a deactivation follow. The number is asserted rather than the shape
        // because the point of this fact is that BOTH write services append to one log.
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

    /// <summary>
    /// A description supplied at creation time lands on the row <b>and</b> produces a second event, because
    /// §8.2's <c>created</c> carries the name and the price only. This is the fact most likely to be got
    /// backwards by widening <c>created</c> instead, which the migration refuses for a stated reason.
    /// </summary>
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

        // Trimmed on the way in, and echoed back as stored so a surface need not re-read.
        Assert.Equal("Pan seared, with greens", result.Description);
        Assert.True(result.DescriptionWasSet);

        MenuItemSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Pan seared, with greens", stored.Description);

        // The first item under this heading, so position 0 — which is 0005's append rule agreeing with
        // 0004's flat rule on the one case where they cannot disagree.
        Assert.Equal(0, stored.DisplayOrder);

        // THREE events now: 'created', then 'section_changed', then 'description_changed'. §8.2 keeps
        // 'created' at the name and the price, so both of the other two facts are recorded beside it —
        // one transaction, three rows, and the log reads "Created as “Salmon” at 18.00 / Filed under
        // Mains / Description set".
        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));

        // Newest-first with the UUIDv7 tiebreak, so the description event is the one this reads.
        Assert.Equal("description_changed", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            "Pan seared, with greens",
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
    }

    /// <summary>
    /// A blank description at creation time is not an event. The no-op rule this class already asserts for
    /// rename and reprice, applied to the one verb where "nothing was typed" is the common case: an
    /// append-only log of "somebody left a field empty" is noise, and §11.4's history is meant to be read.
    /// </summary>
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

        // Two rather than three: the heading is mandatory and always logged, the description is optional
        // and a blank one is not an event at all. That difference is the whole content of this fact.
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

    /// <summary>
    /// Describing an item moves the column and appends a <c>description_changed</c> carrying exactly the
    /// description and nothing else — which §8.2's four paired CHECKs enforce, so getting the payload
    /// wrong here is a loud failure rather than a wrong history.
    /// </summary>
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

        // Three: 'created' and 'section_changed' from the create (0005 makes the heading mandatory and
        // always logged), then this description. The count moved with 0005 and the assertion did not,
        // which is what F-87 is.
        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("description_changed", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            "Lentil, vegan",
            await ScalarAsync<string>("new_description", identifier, cancellationToken));

        // The type carries the description alone. new_name and new_price_amount must be NULL, which is
        // asserted rather than assumed because a caller passing the wrong payload is this file's bug and
        // a CHECK violation is a different failure message from a wrong value.
        Assert.Null(await ScalarAsync<string>("new_name", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<decimal?>("new_price_amount", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<int?>("new_display_order", identifier, cancellationToken));
    }

    /// <summary>
    /// Clearing a description is an ordinary change with an ordinary event. <c>""</c> is a value, not an
    /// absence — which is the entire reason the column is <c>NOT NULL DEFAULT ''</c> rather than nullable
    /// (§8.2): a nullable column could not be tied to its event type by an equality.
    /// </summary>
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

        // created, section_changed and description_changed from the create — 0005 makes the heading a
        // third row on any create with a description — then description_changed for this clear.
        Assert.Equal(4, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("description_changed", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            string.Empty,
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
    }

    /// <summary>
    /// Re-saving the same description writes nothing. Ordinal comparison, so recasing is a real change —
    /// asserted in the second half, because getting that backwards would silently refuse a change every
    /// guest can read.
    /// </summary>
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

        // Same text, differently padded — the trim happens before the comparison.
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

    /// <summary>
    /// Moving an item writes the column and a <c>reordered</c> event carrying the position alone; moving
    /// it to where it already is writes nothing. Positions are absolute, not relative, and not unique.
    /// </summary>
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

        // Three: 'created' and 'section_changed' from the create, then this move.
        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("reordered", await EventTypeAsync(identifier, cancellationToken));
        Assert.Equal(7, await ScalarAsync<int>("new_display_order", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_description", identifier, cancellationToken));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            ReorderMenuItemOutcome.NoChange,
            await Administration().ReorderMenuItemAsync(
                identifier, 7, _administratorIdentifier, cancellationToken));

        // Still three. A move to the position an item already occupies appends nothing.
        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// The move, and the fact that decides whether it is a move or a rewrite of somebody else's ordering.
    ///
    /// <para>The target heading already holds an item at position 0, so an implementation that carried
    /// the moved item's old number across would land it at 0 as well and tie with the pie — which the
    /// schema permits, which the reads would break by name, and which no other assertion in this file
    /// would notice. §7 appends, so the answer is 1.</para>
    ///
    /// <para>Both events are asserted, because §8.2 binds <c>new_display_order</c> to <c>reordered</c>
    /// alone: a move that changed the position without saying so would leave the column and the log
    /// disagreeing, which is the worse of the two failures in an append-only system (ADR-0002).</para>
    /// </summary>
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

        // Four so far: two per create.
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

        // Appended behind the pie rather than keeping the 0 it held in Mains.
        Assert.Equal(1, stored.DisplayOrder);

        // Untouched by a move, all three of them — §7 refiles a dish, it does not re-describe or 86 it.
        Assert.Equal("Soup", stored.Name);
        Assert.Equal(4.50m, stored.PriceAmount);
        Assert.True(stored.IsActive);

        // Six: the four from the two creates, plus this move's section_changed and reordered.
        Assert.Equal(6, await World().CountAsync(CountEventsSql, cancellationToken));

        // Newest first with the UUIDv7 tiebreak, so the newest is the position event and it carries the
        // number the row now holds.
        Assert.Equal("reordered", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal(1, await ScalarAsync<int>("new_display_order", soup, cancellationToken));
    }

    /// <summary>
    /// A move that lands on the position the item already occupied writes <b>one</b> event, not two. The
    /// target heading is empty, so appending returns 0 and the item was at 0 already — the no-op rule
    /// applied to half of one verb, which is the arm most easily left out.
    /// </summary>
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

        // Three: the create's two, plus one section_changed. No 'reordered' for a number that did not
        // move — and the newest event being the section one is how that is observed.
        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("section_changed", await EventTypeAsync(soup, cancellationToken));
        Assert.Equal(puddings, await ScalarAsync<Guid>("new_menu_section_identifier", soup, cancellationToken));
    }

    /// <summary>
    /// Refiling an item under the heading it is already beneath writes nothing. Same rule as renaming to
    /// the current name, and it is worth its own fact because the picker on <c>ManageMenuItem</c> opens
    /// pre-selected on exactly this value, so this is what pressing the button without touching the
    /// dropdown does — the single most likely way this verb is ever called.
    /// </summary>
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

        // Still the create's two.
        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(_sectionIdentifier, (await Directory().GetAsync(soup, cancellationToken))!.MenuSectionIdentifier);
    }

    /// <summary>
    /// A heading that does not exist is reported rather than raised, and the item does not move. Without
    /// this outcome the caller meets PostgreSQL error 23503 from the foreign key, which names a constraint
    /// instead of naming the thing a person did wrong — the same reasoning
    /// <see cref="CreateMenuItemOutcome.MenuSectionNotFound"/> exists for.
    /// </summary>
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

    /// <summary>
    /// An unknown item is reported before the target heading is read at all, so a stale link naming a real
    /// section is still <c>MenuItemNotFound</c> rather than a rollback that looked like a section problem.
    /// </summary>
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

    /// <summary>
    /// Both new verbs report <c>MenuItemNotFound</c> for an identifier nothing has, and neither writes.
    /// One test rather than two: the shape and the reason are identical, and the assertion is that the
    /// event table is untouched.
    /// </summary>
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

    /// <summary>
    /// A negative position is refused by the argument guard, before a connection is opened — so the CHECK
    /// constraint on the column is the second line of defence rather than the first, exactly as the price
    /// bound is on <see cref="DapperMenuAdministration.RepriceMenuItemAsync"/>.
    /// </summary>
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

        // Two, both from the create, and neither from the refusal — which is the fact. The guard runs
        // before a connection is opened, so the count here is exactly what CreateAsync left behind.
        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>Creates an item with no description, which is what most of these tests want.</summary>
    private async Task<Guid> CreateAsync(string name, decimal price, CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, _sectionIdentifier, name, description: null, price, _administratorIdentifier, cancellationToken);

        return identifier;
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

    /// <summary>
    /// One column of an item's <c>created</c> event, named rather than reached by recency.
    ///
    /// <para><b>Why this exists (F-87).</b> Before <c>0005</c> a create wrote one row, so "the newest
    /// event" and "the created event" were the same row and <see cref="ScalarAsync{T}"/> answered both
    /// questions. <c>0005</c> made the heading mandatory and always logged, so a create now writes
    /// <c>created</c> then <c>section_changed</c> at the same instant, ordered by the UUIDv7 tiebreak —
    /// and every payload column on that second row is null by CHECK. Two facts about the create's own
    /// payload therefore started reading the section event and asserting against nothing: a name came
    /// back <c>null</c> and a price came back <c>0</c>, both reported as ordinary value mismatches in a
    /// class whose subject is exactly those two values. <c>CreateWritesTheItemAndItsCreatedEventTogether</c>
    /// had already written this query inline for the same reason; this is that shape hoisted, so the
    /// third fact that needs it does not have to rediscover why.</para>
    ///
    /// <para>A create writes exactly one <c>created</c> row, so no ordering is needed and none is
    /// written — an <c>ORDER BY</c> here would suggest there might be two.</para>
    /// </summary>
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
