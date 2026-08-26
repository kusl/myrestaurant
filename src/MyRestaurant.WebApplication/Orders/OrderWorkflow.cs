using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Orders;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Orders;

public interface IOrderWorkflow
{
    Task<AppendOrderEventResult> SubmitGuestBatchAsync(
        Guid sittingIdentifier,
        Guid guestPersonIdentifier,
        IReadOnlyList<OrderOperation> operations,
        CancellationToken cancellationToken = default);

    Task<AppendOrderEventResult> AppendStaffEventAsync(
        Guid guestOrderIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default);

    Task<AppendOrderEventResult> AppendStaffEventToLivingOrderAsync(
        Guid sittingIdentifier,
        Guid orderOwnerPersonIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default);
}

public sealed class OrderWorkflow : IOrderWorkflow
{
    private readonly IOrderMutations _mutations;
    private readonly IDomainEventBroadcaster _broadcaster;
    private readonly RestaurantMetrics _metrics;

    public OrderWorkflow(
        IOrderMutations mutations,
        IDomainEventBroadcaster broadcaster,
        RestaurantMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(metrics);

        _mutations = mutations;
        _broadcaster = broadcaster;
        _metrics = metrics;
    }

    public async Task<AppendOrderEventResult> SubmitGuestBatchAsync(
        Guid sittingIdentifier,
        Guid guestPersonIdentifier,
        IReadOnlyList<OrderOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission,
            guestPersonIdentifier,
            OrderActorRole.Guest,
            operations);

        AppendOrderEventResult result = await _mutations
            .AppendToLivingOrderAsync(sittingIdentifier, guestPersonIdentifier, proposed, cancellationToken)
            .ConfigureAwait(false);

        AfterCommit(result);
        return result;
    }

    public async Task<AppendOrderEventResult> AppendStaffEventAsync(
        Guid guestOrderIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        AppendOrderEventResult result = await _mutations
            .AppendToOrderAsync(guestOrderIdentifier, proposed, cancellationToken)
            .ConfigureAwait(false);

        AfterCommit(result);
        return result;
    }

    public async Task<AppendOrderEventResult> AppendStaffEventToLivingOrderAsync(
        Guid sittingIdentifier,
        Guid orderOwnerPersonIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        AppendOrderEventResult result = await _mutations
            .AppendToLivingOrderAsync(sittingIdentifier, orderOwnerPersonIdentifier, proposed, cancellationToken)
            .ConfigureAwait(false);

        AfterCommit(result);
        return result;
    }

    private void AfterCommit(AppendOrderEventResult result)
    {
        if (!result.IsAppended)
        {
            return;
        }

        Guid sittingIdentifier = result.SittingIdentifier!.Value;
        Guid guestOrderIdentifier = result.GuestOrderIdentifier!.Value;
        Guid orderEventIdentifier = result.OrderEventIdentifier!.Value;

        if (result.EventType == OrderEventType.GuestSubmission)
        {
            _metrics.RecordGuestSubmissionBatch();
        }

        if (result.LinesAdded > 0)
        {
            _metrics.RecordOrderLinesAdded(result.LinesAdded);
        }

        if (result.LinesRemoved > 0)
        {
            _metrics.RecordOrderLinesRemoved(result.LinesRemoved);
        }

        if (result.LinesFulfilled > 0)
        {
            _metrics.RecordOrderLinesFulfilled(result.LinesFulfilled);
        }

        _broadcaster.Publish(new OrderLinesChanged(sittingIdentifier, guestOrderIdentifier));

        if (result.EventType is OrderEventType.Fulfillment or OrderEventType.FulfillmentReversal)
        {
            _broadcaster.Publish(new LineFulfillmentChanged(sittingIdentifier, guestOrderIdentifier));
        }

        if (result.KitchenNotificationWritten)
        {
            _broadcaster.Publish(new KitchenAlert(orderEventIdentifier, KitchenAlertKind.Initial));
        }
    }
}
