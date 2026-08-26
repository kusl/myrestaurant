using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

public sealed class OrderMutationsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountEventsSql = "SELECT count(*)::int FROM order_event;";
    private const string CountOrdersSql = "SELECT count(*)::int FROM guest_order;";
    private const string CountAddedSql = "SELECT count(*)::int FROM order_operation_line_added;";
    private const string CountNotificationsSql = "SELECT count(*)::int FROM kitchen_notification;";

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 4, 2, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    public OrderMutationsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task GuestSubmission_CreatesTheLivingOrder_AndPricesEveryLineFromTheMenu()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(lineIdentifier, scene.Soup, 2, 999.99m, "  extra  hot  ")),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Appended, result.Outcome);
        Assert.Equal(scene.SittingIdentifier, result.SittingIdentifier);
        Assert.NotNull(result.GuestOrderIdentifier);
        Assert.NotNull(result.OrderEventIdentifier);
        Assert.Equal(1L, result.SequenceNumber);
        Assert.Equal(1, result.LinesAdded);
        Assert.Equal(0, result.LinesRemoved);

        Assert.Equal(1, await World().CountAsync(CountOrdersSql, cancellationToken));

        Assert.Equal(
            4.50m,
            await World().ScalarAsync<decimal>(
                "SELECT unit_price_amount FROM order_operation_line_added WHERE order_line_identifier = @LineIdentifier;",
                new { LineIdentifier = lineIdentifier },
                cancellationToken));

        Assert.Equal(
            "extra  hot",
            await World().ScalarAsync<string>(
                "SELECT customization_note FROM order_operation_line_added WHERE order_line_identifier = @LineIdentifier;",
                new { LineIdentifier = lineIdentifier },
                cancellationToken));

        Assert.NotNull(result.Projection);
        ProjectedOrderLine line = Assert.Single(result.Projection!.Lines);
        Assert.Equal(lineIdentifier, line.OrderLineIdentifier);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(4.50m, line.CurrentUnitPriceAmount);
        Assert.False(line.IsFulfilled);
        Assert.Equal(9.00m, result.Projection.CurrentTotalAmount);
    }

    [Fact]
    public async Task GuestSubmission_WritesExactlyOneKitchenNotification_AndTheSecondSendGetsTheNextSequence()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);

        AppendOrderEventResult first = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null)),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        AppendOrderEventResult second = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(_identifiers.Create(), scene.Salad, 1, 0m, null)),
            cancellationToken);

        Assert.Equal(1L, first.SequenceNumber);
        Assert.Equal(2L, second.SequenceNumber);
        Assert.Equal(first.GuestOrderIdentifier, second.GuestOrderIdentifier);

        Assert.Equal(1, await World().CountAsync(CountOrdersSql, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountNotificationsSql, cancellationToken));
        Assert.Equal(
            2,
            await World().CountAsync(
                "SELECT count(*)::int FROM kitchen_notification WHERE kind = 'initial';",
                cancellationToken));
    }

    [Fact]
    public async Task GuestSubmission_IsRejectedWhole_WhenOneLineIsUnavailable_AndLeavesNoOrderBehind()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        await World().SetMenuItemAsync(scene.Salad, 6.00m, isActive: false, cancellationToken);

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(
                scene.Ada,
                new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null),
                new LineAddedOperation(_identifiers.Create(), scene.Salad, 1, 0m, null)),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Errors, error => error.OperationIndex == 1);
        Assert.DoesNotContain(result.Errors, error => error.OperationIndex == 0);

        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountAddedSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountNotificationsSql, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountOrdersSql, cancellationToken));

        Assert.NotNull(result.Projection);
        Assert.Empty(result.Projection!.Lines);
    }

    [Fact]
    public async Task GuestSubmission_IsRefusedFromSomeoneWhoIsNotAMemberOfTheSitting()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid stranger = await World().AddPersonAsync("mallory", null, cancellationToken);

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            stranger,
            GuestSubmission(stranger, new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null)),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Errors, error => error.OperationIndex == OrderMutationValidator.EventLevel);
        Assert.Equal(0, await World().CountAsync(CountOrdersSql, cancellationToken));
    }

    [Fact]
    public async Task Guest_MayRemoveTheirOwnPendingLine_ButNotOneTheKitchenHasFulfilled()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid pending = _identifiers.Create();
        Guid served = _identifiers.Create();

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(
                scene.Ada,
                new LineAddedOperation(pending, scene.Soup, 1, 0m, null),
                new LineAddedOperation(served, scene.Salad, 1, 0m, null)),
            cancellationToken);

        Guid orderIdentifier = sent.GuestOrderIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
        Assert.Equal(
            AppendOrderEventOutcome.Appended,
            (await Mutations().AppendToOrderAsync(
                orderIdentifier,
                new ProposedOrderEvent(
                    OrderEventType.Fulfillment,
                    scene.Kitchen,
                    OrderActorRole.Kitchen,
                    [new LineFulfilledOperation(served)]),
                cancellationToken)).Outcome);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);

        AppendOrderEventResult refused = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineRemovedOperation(served, "changed my mind")),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, refused.Outcome);
        Assert.Contains(refused.Errors, error => error.OperationIndex == 0);

        AppendOrderEventResult allowed = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineRemovedOperation(pending, "changed my mind")),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Appended, allowed.Outcome);
        Assert.Equal(1, allowed.LinesRemoved);

        ProjectedOrderLine remaining = Assert.Single(allowed.Projection!.Lines);
        Assert.Equal(served, remaining.OrderLineIdentifier);
        Assert.True(remaining.IsFulfilled);

        Assert.Equal(2, await World().CountAsync(CountNotificationsSql, cancellationToken));
    }

    [Fact]
    public async Task Fulfillment_AndItsReversal_FlipTheLineAndNeverAlertTheKitchen()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(lineIdentifier, scene.Soup, 1, 0m, null)),
            cancellationToken);

        Guid orderIdentifier = sent.GuestOrderIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
        AppendOrderEventResult fulfilled = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                scene.Kitchen,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(lineIdentifier)]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Appended, fulfilled.Outcome);
        Assert.Equal(1, fulfilled.LinesFulfilled);
        Assert.False(fulfilled.KitchenNotificationWritten);
        Assert.True(Assert.Single(fulfilled.Projection!.Lines).IsFulfilled);

        Assert.Equal(
            AppendOrderEventOutcome.Rejected,
            (await Mutations().AppendToOrderAsync(
                orderIdentifier,
                new ProposedOrderEvent(
                    OrderEventType.Fulfillment,
                    scene.Kitchen,
                    OrderActorRole.Kitchen,
                    [new LineFulfilledOperation(lineIdentifier)]),
                cancellationToken)).Outcome);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        AppendOrderEventResult reverted = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.FulfillmentReversal,
                scene.Kitchen,
                OrderActorRole.Kitchen,
                [new LineFulfillmentRevertedOperation(lineIdentifier)]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Appended, reverted.Outcome);
        Assert.Equal(1, reverted.LinesFulfillmentReverted);
        Assert.False(Assert.Single(reverted.Projection!.Lines).IsFulfilled);
        Assert.Equal(1, await World().CountAsync(CountNotificationsSql, cancellationToken));
    }

    [Fact]
    public async Task PriceAdjustment_MovesTheLinePrice_AndIsRefusedWithoutAReason()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(lineIdentifier, scene.Soup, 2, 0m, null)),
            cancellationToken);

        Guid orderIdentifier = sent.GuestOrderIdentifier!.Value;

        AppendOrderEventResult refused = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.PriceAdjustment,
                scene.Counter,
                OrderActorRole.Counter,
                [new LinePriceAdjustedOperation(lineIdentifier, 3.00m, "   ")]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, refused.Outcome);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        AppendOrderEventResult adjusted = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.PriceAdjustment,
                scene.Counter,
                OrderActorRole.Counter,
                [new LinePriceAdjustedOperation(lineIdentifier, 3.00m, "  spilled the first bowl  ")]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Appended, adjusted.Outcome);
        Assert.False(adjusted.KitchenNotificationWritten);
        Assert.Equal(3.00m, Assert.Single(adjusted.Projection!.Lines).CurrentUnitPriceAmount);
        Assert.Equal(6.00m, adjusted.Projection.CurrentTotalAmount);

        Assert.Equal(
            "spilled the first bowl",
            await World().ScalarAsync<string>(
                "SELECT reason FROM order_operation_line_price_adjusted WHERE order_line_identifier = @LineIdentifier;",
                new { LineIdentifier = lineIdentifier },
                cancellationToken));
    }

    [Fact]
    public async Task StaffEdit_AlertsTheKitchenFromTheCounter_AndIsSilentFromTheKitchenItself()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null)),
            cancellationToken);

        Guid orderIdentifier = sent.GuestOrderIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        AppendOrderEventResult byCounter = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                scene.Counter,
                OrderActorRole.Counter,
                [new LineAddedOperation(_identifiers.Create(), scene.Salad, 1, 0m, "no dressing")]),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        AppendOrderEventResult byKitchen = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                scene.Kitchen,
                OrderActorRole.Kitchen,
                [new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null)]),
            cancellationToken);

        Assert.True(byCounter.KitchenNotificationWritten);
        Assert.False(byKitchen.KitchenNotificationWritten);
        Assert.Equal(2, await World().CountAsync(CountNotificationsSql, cancellationToken));

        Assert.Equal(3, byKitchen.Projection!.Lines.Count);
        Assert.Equal(4.50m + 6.00m + 4.50m, byKitchen.Projection.CurrentTotalAmount);
    }

    [Fact]
    public async Task ClosedSitting_RefusesAGuestSubmission_ButTakesAnAdministratorCorrection()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(lineIdentifier, scene.Soup, 1, 0m, null)),
            cancellationToken);

        Guid orderIdentifier = sent.GuestOrderIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        await World().CloseSittingAsync(scene.SittingIdentifier, scene.Counter, 4.50m, cancellationToken);

        Assert.Equal(
            AppendOrderEventOutcome.Rejected,
            (await Mutations().AppendToLivingOrderAsync(
                scene.SittingIdentifier,
                scene.Ada,
                GuestSubmission(scene.Ada, new LineAddedOperation(_identifiers.Create(), scene.Salad, 1, 0m, null)),
                cancellationToken)).Outcome);

        Assert.Equal(
            AppendOrderEventOutcome.Rejected,
            (await Mutations().AppendToOrderAsync(
                orderIdentifier,
                new ProposedOrderEvent(
                    OrderEventType.StaffEdit,
                    scene.Counter,
                    OrderActorRole.Counter,
                    [new LineRemovedOperation(lineIdentifier, "comped")]),
                cancellationToken)).Outcome);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        AppendOrderEventResult corrected = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                scene.Administrator,
                OrderActorRole.Administrator,
                [new LineRemovedOperation(lineIdentifier, "comped after the fact")]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Appended, corrected.Outcome);
        Assert.Equal(2L, corrected.SequenceNumber);
        Assert.Empty(corrected.Projection!.Lines);

        Assert.Equal(
            4.50m,
            await World().ScalarAsync<decimal>(
                "SELECT settled_total_amount FROM table_sitting WHERE table_sitting_identifier = @SittingIdentifier;",
                new { SittingIdentifier = scene.SittingIdentifier },
                cancellationToken));
    }

    [Fact]
    public async Task ClosedSitting_WithNoPriorOrder_ReportsOrderNotFound_AndInventsNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        await World().CloseSittingAsync(scene.SittingIdentifier, scene.Counter, 0m, cancellationToken);

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                scene.Administrator,
                OrderActorRole.Administrator,
                [new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null)]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.OrderNotFound, result.Outcome);
        Assert.Equal(0, await World().CountAsync(CountOrdersSql, cancellationToken));
    }

    [Fact]
    public async Task UnknownSitting_AndUnknownOrder_EachReportThemselvesAndWriteNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);

        AppendOrderEventResult noSitting = await Mutations().AppendToLivingOrderAsync(
            _identifiers.Create(),
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(_identifiers.Create(), scene.Soup, 1, 0m, null)),
            cancellationToken);

        AppendOrderEventResult noOrder = await Mutations().AppendToOrderAsync(
            _identifiers.Create(),
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                scene.Kitchen,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(_identifiers.Create())]),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.SittingNotFound, noSitting.Outcome);
        Assert.Null(noSitting.Projection);
        Assert.Equal(AppendOrderEventOutcome.OrderNotFound, noOrder.Outcome);
        Assert.Null(noOrder.Projection);

        Assert.Equal(0, await World().CountAsync(CountOrdersSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AnEmptyEvent_AndALineFromAnotherOrder_AreBothRefused()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        Guid adasLine = _identifiers.Create();

        await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            GuestSubmission(scene.Ada, new LineAddedOperation(adasLine, scene.Soup, 1, 0m, null)),
            cancellationToken);

        AppendOrderEventResult empty = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Ada,
            new ProposedOrderEvent(OrderEventType.GuestSubmission, scene.Ada, OrderActorRole.Guest, []),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, empty.Outcome);
        Assert.Contains(empty.Errors, error => error.OperationIndex == OrderMutationValidator.EventLevel);

        AppendOrderEventResult crossOrder = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.Grace,
            GuestSubmission(scene.Grace, new LineRemovedOperation(adasLine, null)),
            cancellationToken);

        Assert.Equal(AppendOrderEventOutcome.Rejected, crossOrder.Outcome);
        Assert.Contains(crossOrder.Errors, error => error.OperationIndex == 0);
        Assert.Equal(1, await World().CountAsync(CountOrdersSql, cancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;

    private static ProposedOrderEvent GuestSubmission(Guid actor, params OrderOperation[] operations)
        => new(OrderEventType.GuestSubmission, actor, OrderActorRole.Guest, operations);

    private async Task<Scene> ArrangeAsync(CancellationToken cancellationToken)
    {
        OrderTestWorld world = World();

        Guid tableIdentifier = await world.AddTableAsync("Table 1", cancellationToken);
        Guid sittingIdentifier = await world.OpenSittingAsync(tableIdentifier, cancellationToken);

        Guid ada = await world.AddPersonAsync("ada", "Ada", cancellationToken);
        Guid grace = await world.AddPersonAsync("grace", "Grace", cancellationToken);
        await world.JoinAsync(sittingIdentifier, ada, cancellationToken);
        await world.JoinAsync(sittingIdentifier, grace, cancellationToken);

        Guid kitchen = await world.AddPersonAsync("kim", "Kim", cancellationToken);
        Guid counter = await world.AddPersonAsync("cass", "Cass", cancellationToken);
        Guid administrator = await world.AddPersonAsync("adam", "Adam", cancellationToken);

        Guid soup = await world.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        Guid salad = await world.AddMenuItemAsync("Salad", 6.00m, cancellationToken);

        return new Scene(tableIdentifier, sittingIdentifier, ada, grace, kitchen, counter, administrator, soup, salad);
    }

    private sealed record Scene(
        Guid TableIdentifier,
        Guid SittingIdentifier,
        Guid Ada,
        Guid Grace,
        Guid Kitchen,
        Guid Counter,
        Guid Administrator,
        Guid Soup,
        Guid Salad);
}
