using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuAvailability"/> against a real PostgreSQL 17 container —
/// the "86" write behind §11.2's availability panel.
///
/// <para>Two properties matter more than the flag itself. The first is that §7's promise survives:
/// deactivating an item must <b>not</b> remove it from the menu — "the guest sees that the salmon exists
/// and is out, rather than watching it silently vanish" — and
/// <see cref="DeactivatingDoesNotHideTheItemFromTheMenu"/> asserts that against the real directory
/// rather than trusting the two files to agree. The second is that the append-only history stays
/// truthful (ADR-0002): a toggle that changed nothing must write nothing, or §11.4's per-item event
/// history becomes a log of button presses instead of a log of what happened to the salmon.</para>
/// </summary>
public sealed class MenuAvailabilityTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountEventsSql = """
        SELECT count(*)::int FROM menu_item_event;
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 5, 14, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _kitchenIdentifier;

    public MenuAvailabilityTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _kitchenIdentifier = await _world.AddPersonAsync("kim", "Kim", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeactivatingFlipsTheFlagAndLogsTheEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        SetMenuItemAvailabilityResult result = await Availability()
            .SetActiveAsync(salmon, isActive: false, _kitchenIdentifier, cancellationToken);

        Assert.Equal(SetMenuItemAvailabilityOutcome.Changed, result.Outcome);
        Assert.True(result.Changed);
        Assert.True(result.ItemExists);
        Assert.Equal("Salmon", result.Name);
        Assert.False(result.IsActive);

        MenuItemSummary? stored = await Directory().GetAsync(salmon, cancellationToken);
        Assert.NotNull(stored);
        Assert.False(stored.IsActive);

        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));

        string? eventType = await World().ScalarAsync<string>(
            "SELECT event_type FROM menu_item_event WHERE menu_item_identifier = @MenuItemIdentifier;",
            new { MenuItemIdentifier = salmon },
            cancellationToken);

        Assert.Equal("deactivated", eventType);
    }

    [Fact]
    public async Task TheEventRecordsTheActorAndTheInstant()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(7);

        await Availability().SetActiveAsync(salmon, isActive: false, _kitchenIdentifier, cancellationToken);

        Guid actor = await World().ScalarAsync<Guid>(
            "SELECT actor_person_identifier FROM menu_item_event WHERE menu_item_identifier = @MenuItemIdentifier;",
            new { MenuItemIdentifier = salmon },
            cancellationToken);

        DateTime occurredAt = await World().ScalarAsync<DateTime>(
            "SELECT occurred_at FROM menu_item_event WHERE menu_item_identifier = @MenuItemIdentifier;",
            new { MenuItemIdentifier = salmon },
            cancellationToken);

        Assert.Equal(_kitchenIdentifier, actor);
        Assert.Equal(_clock.UtcNow.UtcDateTime, DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc));
    }

    /// <summary>
    /// §7: a deactivated item "stays on the menu marked 'currently unavailable' and cannot be added to a
    /// send". If this ever turns into a delete, the guest watches the salmon vanish and asks a member of
    /// staff what happened to it.
    /// </summary>
    [Fact]
    public async Task DeactivatingDoesNotHideTheItemFromTheMenu()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);
        await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        await Availability().SetActiveAsync(salmon, isActive: false, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        Assert.Equal(2, menu.Count);
        Assert.Contains(menu, item => item.Name == "Salmon" && !item.IsActive);
    }

    /// <summary>
    /// An append-only log of "somebody pressed a button that changed nothing" is noise, and §11.4's
    /// per-item history is meant to be read by a person.
    /// </summary>
    [Fact]
    public async Task DeactivatingTwiceIsANoOpAndWritesNoSecondEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Availability().SetActiveAsync(salmon, isActive: false, _kitchenIdentifier, cancellationToken);

        SetMenuItemAvailabilityResult second = await Availability()
            .SetActiveAsync(salmon, isActive: false, _kitchenIdentifier, cancellationToken);

        Assert.Equal(SetMenuItemAvailabilityOutcome.AlreadyInThatState, second.Outcome);
        Assert.False(second.Changed);
        Assert.True(second.ItemExists);
        Assert.Equal("Salmon", second.Name);
        Assert.False(second.IsActive);

        Assert.Equal(1, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task BringingAnItemBackLogsAnActivatedEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken, isActive: false);

        SetMenuItemAvailabilityResult result = await Availability()
            .SetActiveAsync(salmon, isActive: true, _kitchenIdentifier, cancellationToken);

        Assert.Equal(SetMenuItemAvailabilityOutcome.Changed, result.Outcome);
        Assert.True(result.IsActive);

        string? eventType = await World().ScalarAsync<string>(
            "SELECT event_type FROM menu_item_event WHERE menu_item_identifier = @MenuItemIdentifier;",
            new { MenuItemIdentifier = salmon },
            cancellationToken);

        Assert.Equal("activated", eventType);

        MenuItemSummary? stored = await Directory().GetAsync(salmon, cancellationToken);
        Assert.NotNull(stored);
        Assert.True(stored.IsActive);
    }

    /// <summary>A full off-and-on cycle leaves two events in order, not one overwritten row (ADR-0002).</summary>
    [Fact]
    public async Task TheHistoryKeepsEveryFlip()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Availability().SetActiveAsync(salmon, isActive: false, _kitchenIdentifier, cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(20);
        await Availability().SetActiveAsync(salmon, isActive: true, _kitchenIdentifier, cancellationToken);

        Assert.Equal(2, await World().CountAsync(CountEventsSql, cancellationToken));

        string? latest = await World().ScalarAsync<string>(
            """
            SELECT event_type
            FROM menu_item_event
            WHERE menu_item_identifier = @MenuItemIdentifier
            ORDER BY occurred_at DESC
            LIMIT 1;
            """,
            new { MenuItemIdentifier = salmon },
            cancellationToken);

        Assert.Equal("activated", latest);
    }

    [Fact]
    public async Task AnUnknownItemReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SetMenuItemAvailabilityResult result = await Availability()
            .SetActiveAsync(_identifiers.Create(), isActive: false, _kitchenIdentifier, cancellationToken);

        Assert.Equal(SetMenuItemAvailabilityOutcome.MenuItemNotFound, result.Outcome);
        Assert.False(result.ItemExists);
        Assert.False(result.Changed);
        Assert.Null(result.Name);

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuAvailability Availability() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
