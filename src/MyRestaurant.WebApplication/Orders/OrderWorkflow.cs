using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Orders;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// The web layer's entry point for changing an order (TECHNICAL_SPECIFICATION §6, §9, §10.1, §12).
///
/// <para><see cref="IOrderMutations"/> owns the transaction and stops at commit, because a data-access
/// service has no business knowing about Blazor circuits or OpenTelemetry meters. The two things §6.6
/// step (g) and §12 require happen <em>after</em> that commit, and they happen here: the §9
/// notifications are published to subscribed circuits, and the §12 counters are incremented. A surface
/// calls this and never <see cref="IOrderMutations"/> directly — otherwise the kitchen would not hear
/// about a send, which is a silent failure of the loudest requirement in the specification.</para>
///
/// <para>Nothing is published or counted for a rejected event: §6.5.9 rolls the whole transaction back,
/// so there is no state change to announce and no line to count. The caller re-renders from the fresh
/// projection the result carries.</para>
/// </summary>
public interface IOrderWorkflow
{
    /// <summary>
    /// A guest's batch send (§6.3, §11.1): one <c>guest_submission</c> event owning every staged add and
    /// every marked removal, all-or-nothing. The order is the sender's living order in this sitting and
    /// is created lazily if this is their first send (§6.1).
    /// </summary>
    Task<AppendOrderEventResult> SubmitGuestBatchAsync(
        Guid sittingIdentifier,
        Guid guestPersonIdentifier,
        IReadOnlyList<OrderOperation> operations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Any staff-authored event against a named order — a staff edit, a price adjustment, a fulfillment,
    /// a reversal, or an administrator's post-close correction (§6.3, §6.7, §11.2, §11.3). The caller
    /// supplies the actor's capacity; the §6.2 type↔role rules are enforced inside the transaction and
    /// again by the schema's same-row CHECKs.
    /// </summary>
    Task<AppendOrderEventResult> AppendStaffEventAsync(
        Guid guestOrderIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only implementation of <see cref="IOrderWorkflow"/>: a thin post-commit shell around
/// <see cref="IOrderMutations"/>, in the same spirit as <c>TableJoinTokens</c> — the decision lives
/// below, the metric and the broadcast live here, and neither is buried in a Razor component where it
/// cannot be tested.
/// </summary>
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

        // §6.2: a guest_submission is authored in the `guest` capacity, which is not a stored role — an
        // administrator eating lunch sends their own order exactly like anyone else (§0, §3.7).
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

    /// <summary>
    /// §6.6 step (g) and §12, for a committed event only. Metrics first, then broadcasts, so a
    /// subscriber that re-queries synchronously cannot observe a state change that has not been counted.
    /// </summary>
    private void AfterCommit(AppendOrderEventResult result)
    {
        if (!result.IsAppended)
        {
            return;
        }

        Guid sittingIdentifier = result.SittingIdentifier!.Value;
        Guid guestOrderIdentifier = result.GuestOrderIdentifier!.Value;
        Guid orderEventIdentifier = result.OrderEventIdentifier!.Value;

        // §12. `guest_submission_batches_total` counts sends, not lines — one per accepted batch,
        // including a pure-removal send, which is a send the kitchen still has to hear about (§10.1).
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

        // §9. OrderLinesChanged fires on "any order event commit", so it is unconditional; a fulfillment
        // or reversal additionally raises LineFulfillmentChanged, which the kitchen listens for and the
        // table surface uses to re-badge one line rather than re-render the whole order.
        _broadcaster.Publish(new OrderLinesChanged(sittingIdentifier, guestOrderIdentifier));

        if (result.EventType is OrderEventType.Fulfillment or OrderEventType.FulfillmentReversal)
        {
            _broadcaster.Publish(new LineFulfillmentChanged(sittingIdentifier, guestOrderIdentifier));
        }

        // §10.1: the alert is announced only when a kitchen_notification row actually went in with the
        // event — the transaction decided that, and this must not second-guess it, or the sound and the
        // stored record would drift apart.
        if (result.KitchenNotificationWritten)
        {
            _broadcaster.Publish(new KitchenAlert(orderEventIdentifier, KitchenAlertKind.Initial));
        }
    }
}
