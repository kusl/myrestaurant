using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuSectionAdministration"/> against a real PostgreSQL 17
/// container — §7's create, rename, describe, reorder, and activate for the menu's headings, behind
/// §11.4's menu section.
///
/// <para>The facts worth pinning are the ones the schema cannot state on its own. §8.2's three named
/// paired CHECKs already refuse an event carrying the wrong payload — get that wrong and the database
/// rejects the write, which is the <em>good</em> failure and needs no test to notice. What the database
/// cannot see is whether the pair of rows was written at all, whether a no-op quietly appended an event
/// recording that nothing happened, and whether <c>display_order</c> was assigned or invented. Each of
/// those fails silently and leaves a history that reads plausibly and is wrong, which is the worse of the
/// two failures in an append-only system (ADR-0002).</para>
///
/// <para><see cref="RenamingOnlyTheCapitalisationIsARealChange"/> is the one that would be easy to get
/// backwards. <c>menu_section.name</c> is <c>citext</c>, so the database considers "drinks" and "Drinks"
/// equal for the UNIQUE constraint — which is the whole reason the column has that type. Comparing with
/// those same semantics when deciding whether <em>one</em> section moved would silently refuse a
/// capitalisation fix that every guest can read.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class MenuSectionAdministrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountSectionsSql = """
        SELECT count(*)::int FROM menu_section;
        """;

    private const string CountEventsSql = """
        SELECT count(*)::int FROM menu_section_event;
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 18, 16, 45, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    public MenuSectionAdministrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task CreateWritesTheSectionAndItsCreatedEventTogether()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuSectionResult result = await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", "Served all day", _administratorIdentifier, cancellationToken);

        Assert.True(result.Created);
        Assert.Equal(identifier, result.MenuSectionIdentifier);
        Assert.Equal("Drinks", result.Name);
        Assert.Equal("Served all day", result.Description);

        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Drinks", stored.Name);
        Assert.Equal("Served all day", stored.Description);

        // §7 creates active: a heading nobody can order under is not a section yet.
        Assert.True(stored.IsActive);
        Assert.Equal(_clock.UtcNow, stored.CreatedAt);

        // §8.2's three CHECKs require ALL THREE payload columns for 'created'.
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("created", await LatestEventTypeAsync(identifier, cancellationToken));
        Assert.Equal("Drinks", await ScalarAsync<string>("new_name", identifier, cancellationToken));
        Assert.Equal(
            "Served all day",
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
        Assert.Equal(0, await ScalarAsync<int>("new_display_order", identifier, cancellationToken));
    }

    [Fact]
    public async Task TheCreatedEventRecordsTheActorAndTheInstant()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", description: null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            _administratorIdentifier,
            await ScalarAsync<Guid>("actor_person_identifier", identifier, cancellationToken));

        DateTime occurredAt = await ScalarAsync<DateTime>("occurred_at", identifier, cancellationToken);
        Assert.Equal(_clock.UtcNow.UtcDateTime, DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc));
    }

    /// <summary>
    /// A section created without a description stores <c>""</c>, not NULL — and its <c>created</c> event
    /// carries <c>""</c> too, because §8.2's description CHECK is an equality that a NULL would break.
    /// This is the whole reason the column is <c>NOT NULL DEFAULT ''</c>.
    /// </summary>
    [Fact]
    public async Task ASectionCreatedWithoutADescriptionStoresTheEmptyStringInBothRows()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();

        CreateMenuSectionResult result = await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", description: "   ", _administratorIdentifier, cancellationToken);

        Assert.Equal(string.Empty, result.Description);

        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(string.Empty, stored.Description);

        Assert.Equal(
            string.Empty,
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
    }

    /// <summary>
    /// Positions are assigned by appending, so the first section is 0 and the next is 1 — nothing has to
    /// be told where to go, and a surface that offers no position control still produces a menu in the
    /// order somebody built it in.
    /// </summary>
    [Fact]
    public async Task SectionsAreAppendedAtTheEndOfTheCurrentOrder()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateMenuSectionResult first = await Administration().CreateMenuSectionAsync(
            _identifiers.Create(), "Breakfast", null, _administratorIdentifier, cancellationToken);
        CreateMenuSectionResult second = await Administration().CreateMenuSectionAsync(
            _identifiers.Create(), "Entrees", null, _administratorIdentifier, cancellationToken);
        CreateMenuSectionResult third = await Administration().CreateMenuSectionAsync(
            _identifiers.Create(), "Drinks", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(0, first.DisplayOrder);
        Assert.Equal(1, second.DisplayOrder);
        Assert.Equal(2, third.DisplayOrder);

        // The list is in stored order, which is deliberately not alphabetical: "Breakfast, Entrees,
        // Drinks" is a decision somebody made and ORDER BY name cannot express it.
        IReadOnlyList<MenuSectionSummary> sections = await Directory().ListAsync(cancellationToken);
        Assert.Equal(
            new[] { "Breakfast", "Entrees", "Drinks" },
            sections.Select(section => section.Name).ToArray());
    }

    /// <summary>
    /// Appending reads <c>MAX(display_order) + 1</c> rather than <c>COUNT(*)</c>, so a section that has
    /// been moved out to a high position does not cause the next create to collide with an existing one.
    /// </summary>
    [Fact]
    public async Task AppendingLooksAtTheHighestPositionRatherThanTheRowCount()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid moved = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            moved, "Breakfast", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            ReorderMenuSectionOutcome.Reordered,
            await Administration().ReorderMenuSectionAsync(
                moved, 9, _administratorIdentifier, cancellationToken));

        CreateMenuSectionResult appended = await Administration().CreateMenuSectionAsync(
            _identifiers.Create(), "Drinks", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(10, appended.DisplayOrder);
    }

    /// <summary>
    /// The <c>citext</c> UNIQUE constraint is reported as an outcome rather than thrown, on the same terms
    /// <c>restaurant_table.label</c> already is — and it catches a name differing only in case, which is
    /// the mis-tap the column type exists to refuse (§7).
    /// </summary>
    [Fact]
    public async Task ASecondSectionWithTheSameNameInAnyCaseIsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Administration().CreateMenuSectionAsync(
            _identifiers.Create(), "Drinks", null, _administratorIdentifier, cancellationToken);

        CreateMenuSectionResult duplicate = await Administration().CreateMenuSectionAsync(
            _identifiers.Create(), "drinks", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(CreateMenuSectionOutcome.NameTaken, duplicate.Outcome);
        Assert.False(duplicate.Created);
        Assert.Null(duplicate.Name);

        Assert.Equal(1, await World().CountAsync(CountSectionsSql, cancellationToken));
        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RenameWritesTheNewNameAndItsEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", null, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            RenameMenuSectionOutcome.Renamed,
            await Administration().RenameMenuSectionAsync(
                identifier, "Beverages", _administratorIdentifier, cancellationToken));

        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Beverages", stored.Name);

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal("renamed", await LatestEventTypeAsync(identifier, cancellationToken));
        Assert.Equal("Beverages", await ScalarAsync<string>("new_name", identifier, cancellationToken));

        // §8.2's other two CHECKs: a rename carries the name and nothing else.
        Assert.Null(await ScalarAsync<string>("new_description", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<int?>("new_display_order", identifier, cancellationToken));
    }

    /// <summary>
    /// The comparison is ordinal even though the column is <c>citext</c>. Changing "drinks" to "Drinks"
    /// changes what every guest reads, so it is a rename with an event, not a no-op — see the interface's
    /// ruling on which of the two roles the type plays.
    /// </summary>
    [Fact]
    public async Task RenamingOnlyTheCapitalisationIsARealChange()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "drinks", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            RenameMenuSectionOutcome.Renamed,
            await Administration().RenameMenuSectionAsync(
                identifier, "Drinks", _administratorIdentifier, cancellationToken));

        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Drinks", stored.Name);
        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RenamingToTheSameNameIsANoOpAndWritesNoEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            RenameMenuSectionOutcome.NoChange,
            await Administration().RenameMenuSectionAsync(
                identifier, "  Drinks  ", _administratorIdentifier, cancellationToken));

        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task RenamingToANameAnotherSectionHoldsIsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid first = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            first, "Drinks", null, _administratorIdentifier, cancellationToken);

        Guid second = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            second, "Entrees", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            RenameMenuSectionOutcome.NameTaken,
            await Administration().RenameMenuSectionAsync(
                second, "DRINKS", _administratorIdentifier, cancellationToken));

        MenuSectionSummary? stored = await Directory().GetAsync(second, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Entrees", stored.Name);
        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// Clearing a description is an ordinary change carrying <c>""</c>, not a deletion carrying NULL —
    /// §8.2's description CHECK is an equality, and a NULL payload on a <c>described</c> event would break
    /// it. This is the assertion that makes the <c>NOT NULL DEFAULT ''</c> column worth its awkwardness.
    /// </summary>
    [Fact]
    public async Task ClearingADescriptionWritesAnEventCarryingTheEmptyString()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", "Served until 11am", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            DescribeMenuSectionOutcome.Described,
            await Administration().DescribeMenuSectionAsync(
                identifier, description: null, _administratorIdentifier, cancellationToken));

        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(string.Empty, stored.Description);

        Assert.Equal("described", await LatestEventTypeAsync(identifier, cancellationToken));
        Assert.Equal(
            string.Empty,
            await ScalarAsync<string>("new_description", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_name", identifier, cancellationToken));
    }

    [Fact]
    public async Task DescribingWithTheDescriptionItAlreadyHasIsANoOp()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", "Served all day", _administratorIdentifier, cancellationToken);

        Assert.Equal(
            DescribeMenuSectionOutcome.NoChange,
            await Administration().DescribeMenuSectionAsync(
                identifier, "  Served all day  ", _administratorIdentifier, cancellationToken));

        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task ReorderingWritesThePositionAndItsEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", null, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            ReorderMenuSectionOutcome.Reordered,
            await Administration().ReorderMenuSectionAsync(
                identifier, 3, _administratorIdentifier, cancellationToken));

        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(3, stored.DisplayOrder);

        Assert.Equal("reordered", await LatestEventTypeAsync(identifier, cancellationToken));
        Assert.Equal(3, await ScalarAsync<int>("new_display_order", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_name", identifier, cancellationToken));
    }

    [Fact]
    public async Task ReorderingToThePositionItAlreadyHoldsIsANoOp()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        CreateMenuSectionResult created = await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            ReorderMenuSectionOutcome.NoChange,
            await Administration().ReorderMenuSectionAsync(
                identifier, created.DisplayOrder!.Value, _administratorIdentifier, cancellationToken));

        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// Two sections may share a position — the column is deliberately not UNIQUE (§8.2) — and the reads
    /// break the tie by name so the menu still renders the same way on every request.
    /// </summary>
    [Fact]
    public async Task TwoSectionsMayShareAPositionAndTheTieIsBrokenByName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid zebra = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            zebra, "Zebra", null, _administratorIdentifier, cancellationToken);

        Guid apple = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            apple, "Apple", null, _administratorIdentifier, cancellationToken);

        Assert.Equal(
            ReorderMenuSectionOutcome.Reordered,
            await Administration().ReorderMenuSectionAsync(
                apple, 0, _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuSectionSummary> sections = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            new[] { "Apple", "Zebra" },
            sections.Select(section => section.Name).ToArray());
        Assert.All(sections, section => Assert.Equal(0, section.DisplayOrder));
    }

    /// <summary>
    /// Switching a heading off writes <c>deactivated</c>, which carries no payload at all — the event type
    /// is the fact. Switching it back writes <c>activated</c>, and asking for the state it already holds
    /// writes nothing.
    /// </summary>
    [Fact]
    public async Task ActivationWritesATypeWithNoPayloadAndIsIdempotent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", null, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            MenuSectionActivationOutcome.Changed,
            await Administration().SetMenuSectionActiveAsync(
                identifier, isActive: false, _administratorIdentifier, cancellationToken));

        Assert.Equal("deactivated", await LatestEventTypeAsync(identifier, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_name", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<string>("new_description", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<int?>("new_display_order", identifier, cancellationToken));

        // §7 does not hide a deactivated thing — the row is still there and still read.
        MenuSectionSummary? stored = await Directory().GetAsync(identifier, cancellationToken);
        Assert.NotNull(stored);
        Assert.False(stored.IsActive);

        Assert.Equal(
            MenuSectionActivationOutcome.NoChange,
            await Administration().SetMenuSectionActiveAsync(
                identifier, isActive: false, _administratorIdentifier, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            MenuSectionActivationOutcome.Changed,
            await Administration().SetMenuSectionActiveAsync(
                identifier, isActive: true, _administratorIdentifier, cancellationToken));

        Assert.Equal("activated", await LatestEventTypeAsync(identifier, cancellationToken));
        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// Every verb reports a missing section rather than throwing, and writes nothing — the shape
    /// <see cref="IMenuAdministration"/> and <see cref="Tables.ITableAdministration"/> both already have,
    /// because a surface reaching a stale link needs to render a not-found panel rather than a stack
    /// trace.
    /// </summary>
    [Fact]
    public async Task EveryVerbReportsAMissingSectionAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid absent = _identifiers.Create();

        Assert.Equal(
            RenameMenuSectionOutcome.MenuSectionNotFound,
            await Administration().RenameMenuSectionAsync(
                absent, "Drinks", _administratorIdentifier, cancellationToken));

        Assert.Equal(
            DescribeMenuSectionOutcome.MenuSectionNotFound,
            await Administration().DescribeMenuSectionAsync(
                absent, "Anything", _administratorIdentifier, cancellationToken));

        Assert.Equal(
            ReorderMenuSectionOutcome.MenuSectionNotFound,
            await Administration().ReorderMenuSectionAsync(
                absent, 2, _administratorIdentifier, cancellationToken));

        Assert.Equal(
            MenuSectionActivationOutcome.MenuSectionNotFound,
            await Administration().SetMenuSectionActiveAsync(
                absent, isActive: false, _administratorIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountSectionsSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Null(await Directory().GetAsync(absent, cancellationToken));
    }

    /// <summary>
    /// The 80-character ceiling is the column's own CHECK, refused here rather than at INSERT time so the
    /// message names the field instead of arriving as PostgreSQL error 23514 — the same reason
    /// <see cref="DapperMenuAdministration"/> refuses a price too large for <c>numeric(10,2)</c>.
    /// </summary>
    [Fact]
    public async Task ANameTooLongForTheColumnIsRefusedBeforeTheDatabaseSeesIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await Administration().CreateMenuSectionAsync(
                _identifiers.Create(),
                new string('x', 81),
                null,
                _administratorIdentifier,
                cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Administration().CreateMenuSectionAsync(
                _identifiers.Create(),
                "   ",
                null,
                _administratorIdentifier,
                cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountSectionsSql, cancellationToken));
    }

    [Fact]
    public async Task ANegativePositionIsRefused()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, "Drinks", null, _administratorIdentifier, cancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await Administration().ReorderMenuSectionAsync(
                identifier, -1, _administratorIdentifier, cancellationToken));

        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    /// <summary>
    /// A fresh installation has no sections at all, and that is the correct state rather than an empty
    /// list standing in for a seeded one: §7 seeds nothing, and the administrator names their own
    /// headings.
    /// </summary>
    [Fact]
    public async Task AFreshDatabaseHasNoSections()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Empty(await Directory().ListAsync(cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountSectionsSql, cancellationToken));
    }

    private async Task<string?> LatestEventTypeAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
        => await ScalarAsync<string>("event_type", menuSectionIdentifier, cancellationToken);

    /// <summary>
    /// The newest event for one section. Every test here writes at most one event per section per instant,
    /// so "newest" is unambiguous; the identifier tiebreak matches the reader's ordering.
    /// </summary>
    private async Task<T?> ScalarAsync<T>(
        string column,
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
        => await World().ScalarAsync<T>(
            $"""
            SELECT {column}
            FROM menu_section_event
            WHERE menu_section_identifier = @MenuSectionIdentifier
            ORDER BY occurred_at DESC, menu_section_event_identifier DESC
            LIMIT 1;
            """,
            new { MenuSectionIdentifier = menuSectionIdentifier },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuSectionAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuSectionDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
