using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Sittings;

public sealed class SittingSettlementTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 2, 19, 15, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _tableIdentifier;
    private Guid _sittingIdentifier;
    private Guid _guestIdentifier;
    private Guid _counterIdentifier;
    private Guid _kitchenIdentifier;
    private Guid _soupIdentifier;
    private Guid _steakIdentifier;

    public SittingSettlementTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _tableIdentifier = await _world.AddTableAsync("Table 4", cancellationToken);
        _sittingIdentifier = await _world.OpenSittingAsync(_tableIdentifier, cancellationToken);

        _guestIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
        await _world.JoinAsync(_sittingIdentifier, _guestIdentifier, cancellationToken);

        _counterIdentifier = await _world.AddPersonAsync("cass", "Cass", cancellationToken);
        _kitchenIdentifier = await _world.AddPersonAsync("kim", "Kim", cancellationToken);

        _soupIdentifier = await _world.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        _steakIdentifier = await _world.AddMenuItemAsync("Steak", 21.00m, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task CloseAndSettle_StampsTheTotalTheActorAndTheInstantTogether()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await SendAsync(_guestIdentifier, _soupIdentifier, quantity: 2, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(40);
        DateTimeOffset closedAt = _clock.UtcNow;

        CloseSittingResult result = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Equal(CloseSittingOutcome.Closed, result.Outcome);
        Assert.True(result.IsClosed);
        Assert.Equal(9.00m, result.SettledTotalAmount);
        Assert.Equal(closedAt, result.ClosedAt);
        Assert.Equal(_counterIdentifier, result.ClosedByPersonIdentifier);

        Assert.Equal(9.00m, await StoredTotalAsync(cancellationToken));
        Assert.Equal(closedAt, await StoredClosedAtAsync(cancellationToken));
        Assert.Equal(_counterIdentifier, await StoredClosedByAsync(cancellationToken));
    }

    [Fact]
    public async Task CloseAndSettle_ChargesForStillPendingLinesAndReportsHowManyThereWere()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid soupLine) = await SendAsync(
            _guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);
        await SendAsync(_guestIdentifier, _steakIdentifier, quantity: 1, cancellationToken);

        await FulfillAsync(orderIdentifier, soupLine, cancellationToken);

        CloseSittingResult result = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Equal(CloseSittingOutcome.Closed, result.Outcome);
        Assert.Equal(25.50m, result.SettledTotalAmount);
        Assert.Equal(1, result.PendingLineCountAtClose);
    }

    [Fact]
    public async Task CloseAndSettle_TotalsEveryMembersOrderNotJustTheFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid second = await World().AddPersonAsync("bode", "Bo", cancellationToken);
        await World().JoinAsync(_sittingIdentifier, second, cancellationToken);

        await SendAsync(_guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);
        await SendAsync(second, _steakIdentifier, quantity: 2, cancellationToken);

        CloseSittingResult result = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Equal(46.50m, result.SettledTotalAmount);
    }

    [Fact]
    public async Task CloseAndSettle_HonoursPriceAdjustmentsAndDropsRemovedLines()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid soupLine) = await SendAsync(
            _guestIdentifier, _soupIdentifier, quantity: 2, cancellationToken);
        (_, Guid steakLine) = await SendAsync(
            _guestIdentifier, _steakIdentifier, quantity: 1, cancellationToken);

        await AdjustPriceAsync(orderIdentifier, soupLine, 3.00m, "cold when it arrived", cancellationToken);
        await RemoveLineAsync(orderIdentifier, steakLine, "sent back", cancellationToken);

        CloseSittingResult result = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Equal(6.00m, result.SettledTotalAmount);

        Assert.Equal(1, result.PendingLineCountAtClose);
    }

    [Fact]
    public async Task CloseAndSettle_ASittingNobodyOrderedIn_SettlesAtZero()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CloseSittingResult result = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Equal(CloseSittingOutcome.Closed, result.Outcome);
        Assert.Equal(0m, result.SettledTotalAmount);
        Assert.Equal(0m, await StoredTotalAsync(cancellationToken));
    }

    [Fact]
    public async Task CloseAndSettle_AlreadyClosed_WritesNothingAndReportsTheEarlierClose()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await SendAsync(_guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);

        CloseSittingResult first = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);
        Assert.Equal(CloseSittingOutcome.Closed, first.Outcome);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        CloseSittingResult second = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _kitchenIdentifier, cancellationToken);

        Assert.Equal(CloseSittingOutcome.AlreadyClosed, second.Outcome);
        Assert.False(second.IsClosed);
        Assert.True(second.SittingIsClosed);

        Assert.Equal(first.SettledTotalAmount, second.SettledTotalAmount);
        Assert.Equal(first.ClosedAt, second.ClosedAt);
        Assert.Equal(_counterIdentifier, second.ClosedByPersonIdentifier);
        Assert.Equal(first.ClosedAt, await StoredClosedAtAsync(cancellationToken));
        Assert.Equal(_counterIdentifier, await StoredClosedByAsync(cancellationToken));
    }

    [Fact]
    public async Task CloseAndSettle_UnknownSitting_WritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CloseSittingResult result = await Settlement()
            .CloseAndSettleAsync(_identifiers.Create(), _counterIdentifier, cancellationToken);

        Assert.Equal(CloseSittingOutcome.SittingNotFound, result.Outcome);
        Assert.Null(result.SettledTotalAmount);
        Assert.Null(result.ClosedAt);

        Assert.Null(await StoredClosedAtAsync(cancellationToken));
    }

    [Fact]
    public async Task AfterClose_AGuestSendIsRejectedAndTheStampedTotalDoesNotMove()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await SendAsync(_guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);

        CloseSittingResult closed = await Settlement()
            .CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);
        Assert.Equal(4.50m, closed.SettledTotalAmount);

        AppendOrderEventResult refused = await Mutations().AppendToLivingOrderAsync(
            _sittingIdentifier,
            _guestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                _guestIdentifier,
                OrderActorRole.Guest,
                [new LineAddedOperation(_identifiers.Create(), _steakIdentifier, 1, 0m, null)]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, refused.Outcome);
        Assert.NotEmpty(refused.Errors);
        Assert.Equal(4.50m, await StoredTotalAsync(cancellationToken));
    }

    [Fact]
    public async Task AfterClose_AnAdministratorsCorrection_LeavesTheStampedTotalAlone()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, _) = await SendAsync(
            _guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);

        await Settlement().CloseAndSettleAsync(_sittingIdentifier, _counterIdentifier, cancellationToken);

        AppendOrderEventResult correction = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                _counterIdentifier,
                OrderActorRole.Administrator,
                [new LineAddedOperation(_identifiers.Create(), _steakIdentifier, 1, 0m, "billed late")]),
            cancellationToken);

        Assert.True(correction.IsAppended);

        Assert.Equal(4.50m, await StoredTotalAsync(cancellationToken));
    }

    private async Task<(Guid OrderIdentifier, Guid LineIdentifier)> SendAsync(
        Guid guestIdentifier,
        Guid menuItemIdentifier,
        int quantity,
        CancellationToken cancellationToken)
    {
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            _sittingIdentifier,
            guestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                guestIdentifier,
                OrderActorRole.Guest,
                [new LineAddedOperation(lineIdentifier, menuItemIdentifier, quantity, 0m, null)]),
            cancellationToken);

        Assert.True(result.IsAppended);
        return (result.GuestOrderIdentifier!.Value, lineIdentifier);
    }

    private async Task FulfillAsync(Guid orderIdentifier, Guid lineIdentifier, CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                _kitchenIdentifier,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(lineIdentifier)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private async Task AdjustPriceAsync(
        Guid orderIdentifier,
        Guid lineIdentifier,
        decimal newUnitPrice,
        string reason,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.PriceAdjustment,
                _counterIdentifier,
                OrderActorRole.Counter,
                [new LinePriceAdjustedOperation(lineIdentifier, newUnitPrice, reason)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private async Task RemoveLineAsync(
        Guid orderIdentifier,
        Guid lineIdentifier,
        string reason,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                _counterIdentifier,
                OrderActorRole.Counter,
                [new LineRemovedOperation(lineIdentifier, reason)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private Task<decimal?> StoredTotalAsync(CancellationToken cancellationToken)
        => World().ScalarAsync<decimal?>(
            "SELECT settled_total_amount FROM table_sitting WHERE table_sitting_identifier = @SittingIdentifier;",
            new { SittingIdentifier = _sittingIdentifier },
            cancellationToken);

    private async Task<DateTimeOffset?> StoredClosedAtAsync(CancellationToken cancellationToken)
    {
        DateTime? closedAt = await World().ScalarAsync<DateTime?>(
            "SELECT closed_at FROM table_sitting WHERE table_sitting_identifier = @SittingIdentifier;",
            new { SittingIdentifier = _sittingIdentifier },
            cancellationToken);

        return closedAt is { } value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;
    }

    private Task<Guid?> StoredClosedByAsync(CancellationToken cancellationToken)
        => World().ScalarAsync<Guid?>(
            """
            SELECT closed_by_person_identifier
            FROM table_sitting
            WHERE table_sitting_identifier = @SittingIdentifier;
            """,
            new { SittingIdentifier = _sittingIdentifier },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperSittingSettlement Settlement() => new(_connectionFactory!, _clock);

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;
}
