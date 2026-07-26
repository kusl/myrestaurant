using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Orders;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Unit tests for <see cref="OrderWorkflow"/> — the shell that turns a committed transaction into the
/// §9 notifications and the §12 counters (TECHNICAL_SPECIFICATION §9, §10.1, §12). No database and no
/// container: <see cref="IOrderMutations"/> is a hand-written fake (§16.1 — hand-written fakes, no Moq)
/// returning whatever result the test wants to react to, which is the whole point — the behaviour under
/// test is "what does the web layer do with a result", and that must be checked for results a real
/// database would take a long time to arrange.
///
/// <para>Two of these facts protect requirements that fail silently. If the kitchen alert were published
/// on the workflow's own reading of §10.1 rather than on the transaction's, a change to one rule and not
/// the other would leave the sound and the stored <c>kitchen_notification</c> row disagreeing. And if
/// anything were published for a rejected event, subscribers would re-query for a change that never
/// happened — harmless on a good day, and a phantom order line on a bad one.</para>
/// </summary>
public sealed class OrderWorkflowTests
{
    private static readonly Guid SittingIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000a001");
    private static readonly Guid OrderIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000a002");
    private static readonly Guid EventIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000a003");
    private static readonly Guid PersonIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000a004");
    private static readonly Guid MenuItemIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000a005");
    private static readonly Guid LineIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000a006");

    [Fact]
    public async Task SubmitGuestBatch_ProposesAGuestSubmissionAuthoredByTheSenderInTheGuestCapacity()
    {
        FakeOrderMutations mutations = new(Appended(OrderEventType.GuestSubmission, linesAdded: 1, notified: true));
        RecordingBroadcaster broadcaster = new();

        await Workflow(mutations, broadcaster).SubmitGuestBatchAsync(
            SittingIdentifier,
            PersonIdentifier,
            [new LineAddedOperation(LineIdentifier, MenuItemIdentifier, 1, 0m, null)],
            TestContext.Current.CancellationToken);

        // The living-order entry point, keyed by (sitting, person) — §6.1.
        Assert.Equal(SittingIdentifier, mutations.LastSittingIdentifier);
        Assert.Equal(PersonIdentifier, mutations.LastOrderOwner);
        Assert.Null(mutations.LastGuestOrderIdentifier);

        ProposedOrderEvent proposed = Assert.IsType<ProposedOrderEvent>(mutations.LastProposed);
        Assert.Equal(OrderEventType.GuestSubmission, proposed.EventType);
        Assert.Equal(PersonIdentifier, proposed.ActorPersonIdentifier);

        // §0/§6.2: `guest` is a capacity, not a stored role. An administrator eating lunch sends this way.
        Assert.Equal(OrderActorRole.Guest, proposed.ActorRole);
        Assert.Single(proposed.Operations);
    }

    [Fact]
    public async Task AnAppendedGuestSubmission_PublishesOrderLinesChangedAndTheKitchenAlert()
    {
        FakeOrderMutations mutations = new(Appended(OrderEventType.GuestSubmission, linesAdded: 2, notified: true));
        RecordingBroadcaster broadcaster = new();

        await Workflow(mutations, broadcaster).SubmitGuestBatchAsync(
            SittingIdentifier,
            PersonIdentifier,
            [new LineAddedOperation(LineIdentifier, MenuItemIdentifier, 1, 0m, null)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, broadcaster.Published.Count);

        OrderLinesChanged linesChanged = Assert.IsType<OrderLinesChanged>(broadcaster.Published[0]);
        Assert.Equal(SittingIdentifier, linesChanged.SittingIdentifier);
        Assert.Equal(OrderIdentifier, linesChanged.GuestOrderIdentifier);

        KitchenAlert alert = Assert.IsType<KitchenAlert>(broadcaster.Published[1]);
        Assert.Equal(EventIdentifier, alert.OrderEventIdentifier);
        Assert.Equal(KitchenAlertKind.Initial, alert.Kind);
    }

    [Fact]
    public async Task AFulfillment_AlsoPublishesLineFulfillmentChanged_AndNeverAnAlert()
    {
        FakeOrderMutations mutations = new(
            Appended(OrderEventType.Fulfillment, linesFulfilled: 1, notified: false));
        RecordingBroadcaster broadcaster = new();

        await Workflow(mutations, broadcaster).AppendStaffEventAsync(
            OrderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                PersonIdentifier,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(LineIdentifier)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(OrderIdentifier, mutations.LastGuestOrderIdentifier);
        Assert.Null(mutations.LastSittingIdentifier);

        // §9: any order event commit is an OrderLinesChanged; a fulfillment is additionally a
        // LineFulfillmentChanged, which is what lets the table re-badge one line instead of re-rendering.
        Assert.Equal(2, broadcaster.Published.Count);
        Assert.IsType<OrderLinesChanged>(broadcaster.Published[0]);
        Assert.IsType<LineFulfillmentChanged>(broadcaster.Published[1]);
        Assert.DoesNotContain(broadcaster.Published, notification => notification is KitchenAlert);
    }

    [Fact]
    public async Task AStaffEditTheTransactionDidNotNotifyOn_PublishesNoAlert()
    {
        // §10.1: the kitchen's own staff edit writes no kitchen_notification row, so nothing sounds.
        FakeOrderMutations mutations = new(Appended(OrderEventType.StaffEdit, linesAdded: 1, notified: false));
        RecordingBroadcaster broadcaster = new();

        await Workflow(mutations, broadcaster).AppendStaffEventAsync(
            OrderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                PersonIdentifier,
                OrderActorRole.Kitchen,
                [new LineAddedOperation(LineIdentifier, MenuItemIdentifier, 1, 0m, null)]),
            TestContext.Current.CancellationToken);

        DomainNotification only = Assert.Single(broadcaster.Published);
        Assert.IsType<OrderLinesChanged>(only);
    }

    [Theory]
    [InlineData(AppendOrderEventOutcome.Rejected)]
    [InlineData(AppendOrderEventOutcome.SittingNotFound)]
    [InlineData(AppendOrderEventOutcome.OrderNotFound)]
    public async Task AnEventThatDidNotCommit_PublishesNothing(AppendOrderEventOutcome outcome)
    {
        FakeOrderMutations mutations = new(new AppendOrderEventResult(
            outcome,
            SittingIdentifier,
            OrderIdentifier,
            OrderEventIdentifier: null,
            SequenceNumber: null,
            OrderEventType.GuestSubmission,
            LinesAdded: 0,
            LinesRemoved: 0,
            LinesFulfilled: 0,
            LinesFulfillmentReverted: 0,
            KitchenNotificationWritten: false,
            [new OrderMutationError(0, "nope")],
            Projection: null));

        RecordingBroadcaster broadcaster = new();

        AppendOrderEventResult result = await Workflow(mutations, broadcaster).SubmitGuestBatchAsync(
            SittingIdentifier,
            PersonIdentifier,
            [new LineAddedOperation(LineIdentifier, MenuItemIdentifier, 1, 0m, null)],
            TestContext.Current.CancellationToken);

        // Nothing committed, so there is nothing to announce — and the caller still gets the reasons.
        Assert.Empty(broadcaster.Published);
        Assert.False(result.IsAppended);
        Assert.Single(result.Errors);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static OrderWorkflow Workflow(IOrderMutations mutations, IDomainEventBroadcaster broadcaster)
    {
        // A real RestaurantMetrics, not a stub: it is sealed and has no interface, so the only way to be
        // sure the §12 call sites run at all is to let them run against real instruments. AddMetrics()
        // supplies the meter factory it takes, exactly as Program.cs does.
        ServiceProvider provider = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<RestaurantMetrics>()
            .BuildServiceProvider();

        return new OrderWorkflow(mutations, broadcaster, provider.GetRequiredService<RestaurantMetrics>());
    }

    private static AppendOrderEventResult Appended(
        OrderEventType eventType,
        int linesAdded = 0,
        int linesRemoved = 0,
        int linesFulfilled = 0,
        bool notified = false)
        => new(
            AppendOrderEventOutcome.Appended,
            SittingIdentifier,
            OrderIdentifier,
            EventIdentifier,
            SequenceNumber: 1,
            eventType,
            linesAdded,
            linesRemoved,
            linesFulfilled,
            LinesFulfillmentReverted: 0,
            notified,
            [],
            new ProjectedOrder(OrderIdentifier, [], 0, 0, 0m, null, null));

    private sealed class FakeOrderMutations : IOrderMutations
    {
        private readonly AppendOrderEventResult _result;

        public FakeOrderMutations(AppendOrderEventResult result) => _result = result;

        public Guid? LastSittingIdentifier { get; private set; }

        public Guid? LastOrderOwner { get; private set; }

        public Guid? LastGuestOrderIdentifier { get; private set; }

        public ProposedOrderEvent? LastProposed { get; private set; }

        public Task<AppendOrderEventResult> AppendToLivingOrderAsync(
            Guid sittingIdentifier,
            Guid orderOwnerPersonIdentifier,
            ProposedOrderEvent proposed,
            CancellationToken cancellationToken = default)
        {
            LastSittingIdentifier = sittingIdentifier;
            LastOrderOwner = orderOwnerPersonIdentifier;
            LastProposed = proposed;
            return Task.FromResult(_result);
        }

        public Task<AppendOrderEventResult> AppendToOrderAsync(
            Guid guestOrderIdentifier,
            ProposedOrderEvent proposed,
            CancellationToken cancellationToken = default)
        {
            LastGuestOrderIdentifier = guestOrderIdentifier;
            LastProposed = proposed;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingBroadcaster : IDomainEventBroadcaster
    {
        public List<DomainNotification> Published { get; } = [];

        public void Publish(DomainNotification notification) => Published.Add(notification);

        public IDisposable Subscribe(Action<DomainNotification> handler) => new NoSubscription();

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose()
            {
                // Nothing subscribes in these tests; the token exists only to satisfy the contract.
            }
        }
    }
}
