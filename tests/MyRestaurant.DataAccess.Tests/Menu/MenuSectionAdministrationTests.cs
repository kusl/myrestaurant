using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

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

        Assert.True(stored.IsActive);
        Assert.Equal(_clock.UtcNow, stored.CreatedAt);

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

        IReadOnlyList<MenuSectionSummary> sections = await Directory().ListAsync(cancellationToken);
        Assert.Equal(
            new[] { "Breakfast", "Entrees", "Drinks" },
            sections.Select(section => section.Name).ToArray());
    }

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

        Assert.Null(await ScalarAsync<string>("new_description", identifier, cancellationToken));
        Assert.Null(await ScalarAsync<int?>("new_display_order", identifier, cancellationToken));
    }

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
