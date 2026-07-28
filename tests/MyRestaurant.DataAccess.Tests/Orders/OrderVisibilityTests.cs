using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

/// <summary>
/// Integration tests for <see cref="DapperOrderVisibility"/> against a real PostgreSQL 17 container —
/// TECHNICAL_SPECIFICATION §6.8's two writes.
///
/// <para>Three properties are being pinned here, and the third is the one that matters most. The first is
/// that the four refusals refuse and write nothing: not the owner, sitting still open, already hidden, no
/// such order. The second is that the append-only shape holds — a hide followed by an unhide followed by
/// a hide leaves three rows, not one row flipped twice, because §6.8's log has to be able to say that a
/// record went round the loop. The third is that <c>order_visibility_current</c> and this service agree
/// about which event is latest: the view's <c>DISTINCT ON … ORDER BY occurred_at DESC, identifier
/// DESC</c> is the definition of "hidden", and a writer that decided otherwise would put an order in the
/// administrator's hidden list and on its owner's history page at the same moment.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class OrderVisibilityTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    /// <summary>Counts every visibility row in the database — the "nothing was written" assertions.</summary>
    private const string CountVisibilityEventsSql = """
        SELECT count(*)::int FROM order_visibility_event;
        """;

    /// <summary>Reads the current flag the way the view defines it, without going through the service.</summary>
    private const string CurrentFlagSql = """
        SELECT order_visibility_current.is_hidden
        FROM order_visibility_current
        WHERE order_visibility_current.guest_order_identifier = @GuestOrderIdentifier;
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 11, 19, 30, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _adaIdentifier;
    private Guid _bodeIdentifier;
    private Guid _counterIdentifier;
    private Guid _administratorIdentifier;
    private Guid _soupIdentifier;

    public OrderVisibilityTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        // Three characters minimum: person.username carries CHECK (char_length BETWEEN 3 AND 64) (§8.2).
        _adaIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
        _bodeIdentifier = await _world.AddPersonAsync("bode", "Bo", cancellationToken);
        _counterIdentifier = await _world.AddPersonAsync("cass", "Cass Okonkwo", cancellationToken);
        _administratorIdentifier = await _world.AddPersonAsync("mira", "Mira Adeyemi", cancellationToken);

        _soupIdentifier = await _world.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Hide_OnASettledSittingByTheOwner_AppendsOneHiddenRowAndFlipsTheView()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledOrderAsync("Table 1", _adaIdentifier, cancellationToken);

        HideOrderResult result = await Visibility().HideAsync(
            orderIdentifier, _adaIdentifier, cancellationToken);

        Assert.Equal(HideOrderOutcome.Hidden, result.Outcome);
        Assert.True(result.IsHidden);
        Assert.True(result.OrderIsHidden);
        Assert.Equal(orderIdentifier, result.GuestOrderIdentifier);
        Assert.Equal(_adaIdentifier, result.OwnerPersonIdentifier);
        Assert.NotNull(result.SittingIdentifier);
        Assert.Equal(_clock.UtcNow, result.OccurredAt);

        Assert.Equal(1, await CountVisibilityEventsAsync(cancellationToken));
        Assert.True(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

    /// <summary>
    /// §6.8 gives the hide to the owner. A page cannot be the check: an identifier in a form field is not
    /// a permission, so the service refuses under its own lock.
    /// </summary>
    [Fact]
    public async Task Hide_BySomebodyWhoIsNotTheOwner_IsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledOrderAsync("Table 2", _adaIdentifier, cancellationToken);

        HideOrderResult result = await Visibility().HideAsync(
            orderIdentifier, _bodeIdentifier, cancellationToken);

        Assert.Equal(HideOrderOutcome.NotTheOwner, result.Outcome);
        Assert.False(result.IsHidden);
        Assert.False(result.OrderIsHidden);

        // The owner is still reported, because the caller needs to know whose order it was to say anything
        // sensible — but nothing was written.
        Assert.Equal(_adaIdentifier, result.OwnerPersonIdentifier);
        Assert.Null(result.OccurredAt);
        Assert.Equal(0, await CountVisibilityEventsAsync(cancellationToken));
    }

    /// <summary>
    /// §6.8: "Hiding applies to an order in a <b>closed</b> sitting." While the table is still eating, the
    /// order is the live one the surface is built around.
    /// </summary>
    [Fact]
    public async Task Hide_WhileTheSittingIsStillOpen_IsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 3", cancellationToken, _adaIdentifier);
        Guid orderIdentifier = await SendAsync(sittingIdentifier, _adaIdentifier, cancellationToken);

        HideOrderResult result = await Visibility().HideAsync(
            orderIdentifier, _adaIdentifier, cancellationToken);

        Assert.Equal(HideOrderOutcome.SittingStillOpen, result.Outcome);
        Assert.Equal(sittingIdentifier, result.SittingIdentifier);
        Assert.Equal(0, await CountVisibilityEventsAsync(cancellationToken));
        Assert.Null(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

    /// <summary>
    /// A second tap is not a failure — the order is in the state the person asked for — but it must not
    /// append a second row, or the log would claim it was hidden twice.
    /// </summary>
    [Fact]
    public async Task Hide_Twice_ReportsAlreadyHiddenAndLeavesOneRow()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledOrderAsync("Table 4", _adaIdentifier, cancellationToken);

        Assert.True((await Visibility().HideAsync(orderIdentifier, _adaIdentifier, cancellationToken))
            .IsHidden);

        HideOrderResult second = await Visibility().HideAsync(
            orderIdentifier, _adaIdentifier, cancellationToken);

        Assert.Equal(HideOrderOutcome.AlreadyHidden, second.Outcome);
        Assert.False(second.IsHidden);

        // Still hidden, and still hidden by exactly one row.
        Assert.True(second.OrderIsHidden);
        Assert.Null(second.OccurredAt);
        Assert.Equal(1, await CountVisibilityEventsAsync(cancellationToken));
    }

    [Fact]
    public async Task Hide_OfAnOrderThatDoesNotExist_ReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HideOrderResult result = await Visibility().HideAsync(
            _identifiers.Create(), _adaIdentifier, cancellationToken);

        Assert.Equal(HideOrderOutcome.OrderNotFound, result.Outcome);
        Assert.Null(result.SittingIdentifier);
        Assert.Null(result.OwnerPersonIdentifier);
        Assert.Equal(0, await CountVisibilityEventsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unhide_OfAHiddenOrder_AppendsAnUnhiddenRowAndFlipsTheViewBack()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledOrderAsync("Table 5", _adaIdentifier, cancellationToken);
        await Visibility().HideAsync(orderIdentifier, _adaIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddHours(2);

        UnhideOrderResult result = await Visibility().UnhideAsync(
            orderIdentifier, _administratorIdentifier, cancellationToken);

        Assert.Equal(UnhideOrderOutcome.Unhidden, result.Outcome);
        Assert.True(result.IsUnhidden);
        Assert.Equal(_clock.UtcNow, result.OccurredAt);

        // The owner is reported, not the actor: the caller's sentence is about whose history just changed.
        Assert.Equal(_adaIdentifier, result.OwnerPersonIdentifier);

        // Two rows, not one flipped: the log says it was hidden and then restored (ADR-0002).
        Assert.Equal(2, await CountVisibilityEventsAsync(cancellationToken));
        Assert.False(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

    /// <summary>
    /// Two administrators pressing Unhide at once: one appends, the other is told somebody got there
    /// first. Reported as fact rather than as an error — the record is visible, which is what was asked.
    /// </summary>
    [Fact]
    public async Task Unhide_OfAnOrderThatIsNotHidden_ReportsNotHiddenAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledOrderAsync("Table 6", _adaIdentifier, cancellationToken);

        UnhideOrderResult result = await Visibility().UnhideAsync(
            orderIdentifier, _administratorIdentifier, cancellationToken);

        Assert.Equal(UnhideOrderOutcome.NotHidden, result.Outcome);
        Assert.False(result.IsUnhidden);
        Assert.Equal(_adaIdentifier, result.OwnerPersonIdentifier);
        Assert.Equal(0, await CountVisibilityEventsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unhide_OfAnOrderThatDoesNotExist_ReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        UnhideOrderResult result = await Visibility().UnhideAsync(
            _identifiers.Create(), _administratorIdentifier, cancellationToken);

        Assert.Equal(UnhideOrderOutcome.OrderNotFound, result.Outcome);
        Assert.Null(result.SittingIdentifier);
        Assert.Equal(0, await CountVisibilityEventsAsync(cancellationToken));
    }

    /// <summary>
    /// The whole loop. Three rows, and the current flag is the third — which is only true if this service
    /// and <c>order_visibility_current</c> agree on what "latest" means.
    /// </summary>
    [Fact]
    public async Task HideUnhideHide_LeavesThreeRowsAndTheOrderHidden()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledOrderAsync("Table 7", _adaIdentifier, cancellationToken);

        Assert.True((await Visibility().HideAsync(orderIdentifier, _adaIdentifier, cancellationToken))
            .IsHidden);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        Assert.True((await Visibility()
            .UnhideAsync(orderIdentifier, _administratorIdentifier, cancellationToken)).IsUnhidden);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        Assert.True((await Visibility().HideAsync(orderIdentifier, _adaIdentifier, cancellationToken))
            .IsHidden);

        Assert.Equal(3, await CountVisibilityEventsAsync(cancellationToken));
        Assert.True(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

    /// <summary>
    /// Hiding one order must not hide anybody else's. The two orders are on the same sitting on purpose:
    /// the write is keyed on the order, not the sitting, and a stray join in the lock statement would show
    /// up exactly here.
    /// </summary>
    [Fact]
    public async Task Hide_TouchesOnlyThatPersonsOrder()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync(
            "Table 8", cancellationToken, _adaIdentifier, _bodeIdentifier);

        Guid adaOrder = await SendAsync(sittingIdentifier, _adaIdentifier, cancellationToken);
        Guid bodeOrder = await SendAsync(sittingIdentifier, _bodeIdentifier, cancellationToken);

        await World().CloseSittingAsync(
            sittingIdentifier, _counterIdentifier, 13.50m, cancellationToken);

        Assert.True((await Visibility().HideAsync(adaOrder, _adaIdentifier, cancellationToken)).IsHidden);

        Assert.True(await CurrentFlagAsync(adaOrder, cancellationToken));
        Assert.Null(await CurrentFlagAsync(bodeOrder, cancellationToken));
        Assert.Equal(1, await CountVisibilityEventsAsync(cancellationToken));
    }

    /// <summary>
    /// An administrator may unhide an order on a sitting that is somehow still open. Deliberate asymmetry
    /// with <see cref="Hide_WhileTheSittingIsStillOpen_IsRefusedAndWritesNothing"/>: the open check exists
    /// to stop a guest hiding a live order, and an administrator is here precisely to undo states that
    /// should not exist.
    /// </summary>
    [Fact]
    public async Task Unhide_WorksEvenIfTheSittingIsOpenAgainstExpectation()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 9", cancellationToken, _adaIdentifier);
        Guid orderIdentifier = await SendAsync(sittingIdentifier, _adaIdentifier, cancellationToken);

        // Arranged directly: the service will not produce this state, which is the point.
        await World().AddVisibilityEventAsync(
            orderIdentifier, _adaIdentifier, "hidden", cancellationToken);
        Assert.True(await CurrentFlagAsync(orderIdentifier, cancellationToken));

        UnhideOrderResult result = await Visibility().UnhideAsync(
            orderIdentifier, _administratorIdentifier, cancellationToken);

        Assert.Equal(UnhideOrderOutcome.Unhidden, result.Outcome);
        Assert.False(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

    // ---- arrangement ------------------------------------------------------------------------------

    private async Task<Guid> SettledOrderAsync(
        string tableLabel,
        Guid guestIdentifier,
        CancellationToken cancellationToken)
    {
        Guid sittingIdentifier = await OpenTableAsync(tableLabel, cancellationToken, guestIdentifier);
        Guid orderIdentifier = await SendAsync(sittingIdentifier, guestIdentifier, cancellationToken);

        await World().CloseSittingAsync(
            sittingIdentifier, _counterIdentifier, 9.00m, cancellationToken);

        return orderIdentifier;
    }

    private async Task<Guid> OpenTableAsync(
        string label,
        CancellationToken cancellationToken,
        params Guid[] members)
    {
        Guid tableIdentifier = await World().AddTableAsync(label, cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);

        foreach (Guid member in members)
        {
            await World().JoinAsync(sittingIdentifier, member, cancellationToken);
        }

        return sittingIdentifier;
    }

    private async Task<Guid> SendAsync(
        Guid sittingIdentifier,
        Guid guestIdentifier,
        CancellationToken cancellationToken)
    {
        // The zero unit price is deliberate: §6.5.4 has the transaction price the line from the menu row
        // it reads under the lock, so anything sent here is discarded.
        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            guestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                guestIdentifier,
                OrderActorRole.Guest,
                [new LineAddedOperation(_identifiers.Create(), _soupIdentifier, 2, 0m, null)]),
            cancellationToken);

        Assert.True(result.IsAppended);
        return result.GuestOrderIdentifier!.Value;
    }

    // ---- assertion helpers ------------------------------------------------------------------------

    private async Task<int> CountVisibilityEventsAsync(CancellationToken cancellationToken)
        => await World().CountAsync(CountVisibilityEventsSql, cancellationToken);

    /// <summary>
    /// The current flag straight from the view: <c>true</c> hidden, <c>false</c> explicitly unhidden, and
    /// <c>null</c> when no visibility event exists at all. The third case is distinguished on purpose —
    /// "never touched" and "unhidden" read the same to a surface but not to a test.
    /// </summary>
    private async Task<bool?> CurrentFlagAsync(Guid guestOrderIdentifier, CancellationToken cancellationToken)
        => await World().ScalarAsync<bool?>(
            CurrentFlagSql,
            new { GuestOrderIdentifier = guestOrderIdentifier },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperOrderVisibility Visibility() => new(_connectionFactory!, _clock, _identifiers);

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;
}
