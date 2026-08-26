using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

public sealed class OrderVisibilityTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountVisibilityEventsSql = """
        SELECT count(*)::int FROM order_visibility_event;
        """;

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

        Assert.Equal(_adaIdentifier, result.OwnerPersonIdentifier);
        Assert.Null(result.OccurredAt);
        Assert.Equal(0, await CountVisibilityEventsAsync(cancellationToken));
    }

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

        Assert.Equal(_adaIdentifier, result.OwnerPersonIdentifier);

        Assert.Equal(2, await CountVisibilityEventsAsync(cancellationToken));
        Assert.False(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

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

    [Fact]
    public async Task Unhide_WorksEvenIfTheSittingIsOpenAgainstExpectation()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 9", cancellationToken, _adaIdentifier);
        Guid orderIdentifier = await SendAsync(sittingIdentifier, _adaIdentifier, cancellationToken);

        await World().AddVisibilityEventAsync(
            orderIdentifier, _adaIdentifier, "hidden", cancellationToken);
        Assert.True(await CurrentFlagAsync(orderIdentifier, cancellationToken));

        UnhideOrderResult result = await Visibility().UnhideAsync(
            orderIdentifier, _administratorIdentifier, cancellationToken);

        Assert.Equal(UnhideOrderOutcome.Unhidden, result.Outcome);
        Assert.False(await CurrentFlagAsync(orderIdentifier, cancellationToken));
    }

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

    private async Task<int> CountVisibilityEventsAsync(CancellationToken cancellationToken)
        => await World().CountAsync(CountVisibilityEventsSql, cancellationToken);

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
