namespace MyRestaurant.Domain.Orders;

/// <summary>
/// A current, non-removed line as seen by the read side — the domain equivalent of the
/// <c>order_current_line</c> view (TECHNICAL_SPECIFICATION §8.3, §8.5). The menu item name
/// (a read-time join in the view) is intentionally omitted; it is not part of the fold's
/// equivalence contract, which covers the line set, prices, and fulfillment flags.
/// </summary>
public sealed record ProjectedOrderLine(
    Guid GuestOrderIdentifier,
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    int Quantity,
    decimal CurrentUnitPriceAmount,
    string? CustomizationNote,
    bool IsFulfilled,
    DateTimeOffset AddedAt,
    Guid AddedByOrderEventIdentifier)
{
    /// <summary>Extended line price at the current unit price (quantity × current unit price).</summary>
    public decimal LineTotalAmount => Quantity * CurrentUnitPriceAmount;
}

/// <summary>
/// The folded state of one living order — the domain equivalent of <c>order_current_state</c>
/// plus its current lines (TECHNICAL_SPECIFICATION §8.3, §8.5). The total <em>includes</em>
/// still-pending lines, matching <c>sitting_bill</c>/<c>order_current_state</c> (§8.3).
/// </summary>
public sealed record ProjectedOrder(
    Guid GuestOrderIdentifier,
    IReadOnlyList<ProjectedOrderLine> Lines,
    int PendingLineCount,
    int FulfilledLineCount,
    decimal CurrentTotalAmount,
    DateTimeOffset? FirstSubmittedAt,
    DateTimeOffset? LastEventAt);

/// <summary>
/// The pure fold from an order's event log to its current projection (TECHNICAL_SPECIFICATION
/// §8.5): <c>FromEvents</c> yields the same line set, prices, and fulfillment flags as the SQL
/// projection views, and integration tests assert view output ≡ fold output on randomized
/// sequences. Neither the fold nor the views are the source of truth — the event tables are.
/// </summary>
public static class OrderProjection
{
    public static ProjectedOrder FromEvents(IReadOnlyList<OrderEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        IReadOnlyDictionary<Guid, LineState> states = BuildLineStates(events);

        List<ProjectedOrderLine> lines = states.Values
            .Where(state => !state.IsRemoved)
            .Select(state => new ProjectedOrderLine(
                state.GuestOrderIdentifier,
                state.OrderLineIdentifier,
                state.MenuItemIdentifier,
                state.Quantity,
                state.CurrentUnitPriceAmount,
                state.CustomizationNote,
                state.IsFulfilled,
                state.AddedAt,
                state.AddedByOrderEventIdentifier))
            .OrderBy(line => line.AddedAt)
            .ThenBy(line => line.OrderLineIdentifier)
            .ToList();

        Guid guestOrderIdentifier = events.Count > 0 ? events[0].GuestOrderIdentifier : Guid.Empty;

        DateTimeOffset? firstSubmittedAt = events
            .Where(orderEvent => orderEvent.EventType == OrderEventType.GuestSubmission)
            .Select(orderEvent => (DateTimeOffset?)orderEvent.OccurredAt)
            .DefaultIfEmpty(null)
            .Min();

        DateTimeOffset? lastEventAt = events
            .Select(orderEvent => (DateTimeOffset?)orderEvent.OccurredAt)
            .DefaultIfEmpty(null)
            .Max();

        return new ProjectedOrder(
            guestOrderIdentifier,
            lines,
            PendingLineCount: lines.Count(line => !line.IsFulfilled),
            FulfilledLineCount: lines.Count(line => line.IsFulfilled),
            CurrentTotalAmount: lines.Sum(line => line.LineTotalAmount),
            firstSubmittedAt,
            lastEventAt);
    }

    /// <summary>
    /// Folds the full per-line lifecycle, <em>including removed lines</em> — the richer view the
    /// mutation validator needs (adding-event metadata, removal, fulfillment). Events are folded
    /// in ascending sequence order, so "latest by sequence wins" for prices and fulfillment
    /// flips, matching the LATERAL sub-selects of <c>order_current_line</c> (§8.3).
    /// </summary>
    internal static IReadOnlyDictionary<Guid, LineState> BuildLineStates(IReadOnlyList<OrderEvent> events)
    {
        Dictionary<Guid, LineState> states = [];

        foreach (OrderEvent orderEvent in events.OrderBy(orderEvent => orderEvent.SequenceNumber))
        {
            foreach (OrderOperation operation in orderEvent.Operations)
            {
                switch (operation)
                {
                    case LineAddedOperation added when !states.ContainsKey(added.OrderLineIdentifier):
                        states[added.OrderLineIdentifier] = new LineState
                        {
                            OrderLineIdentifier = added.OrderLineIdentifier,
                            GuestOrderIdentifier = orderEvent.GuestOrderIdentifier,
                            MenuItemIdentifier = added.MenuItemIdentifier,
                            Quantity = added.Quantity,
                            OriginalUnitPriceAmount = added.UnitPriceAmount,
                            CurrentUnitPriceAmount = added.UnitPriceAmount,
                            CustomizationNote = added.CustomizationNote,
                            AddedAt = orderEvent.OccurredAt,
                            AddedByOrderEventIdentifier = orderEvent.OrderEventIdentifier,
                            AddedByEventType = orderEvent.EventType,
                            AddedByActorPersonIdentifier = orderEvent.ActorPersonIdentifier,
                            AddedByActorRole = orderEvent.ActorRole,
                        };
                        break;

                    case LineRemovedOperation removed when states.TryGetValue(removed.OrderLineIdentifier, out LineState? removedState):
                        removedState.IsRemoved = true;
                        break;

                    case LinePriceAdjustedOperation adjusted when states.TryGetValue(adjusted.OrderLineIdentifier, out LineState? adjustedState):
                        adjustedState.CurrentUnitPriceAmount = adjusted.NewUnitPriceAmount;
                        break;

                    case LineFulfilledOperation fulfilled when states.TryGetValue(fulfilled.OrderLineIdentifier, out LineState? fulfilledState):
                        fulfilledState.IsFulfilled = true;
                        break;

                    case LineFulfillmentRevertedOperation reverted when states.TryGetValue(reverted.OrderLineIdentifier, out LineState? revertedState):
                        revertedState.IsFulfilled = false;
                        break;
                }
            }
        }

        return states;
    }
}

/// <summary>Mutable per-line accumulator used only inside the fold and the validator.</summary>
internal sealed class LineState
{
    public required Guid OrderLineIdentifier { get; init; }
    public required Guid GuestOrderIdentifier { get; init; }
    public required Guid MenuItemIdentifier { get; init; }
    public required int Quantity { get; init; }
    public required decimal OriginalUnitPriceAmount { get; init; }
    public required string? CustomizationNote { get; init; }
    public required DateTimeOffset AddedAt { get; init; }
    public required Guid AddedByOrderEventIdentifier { get; init; }
    public required OrderEventType AddedByEventType { get; init; }
    public required Guid AddedByActorPersonIdentifier { get; init; }
    public required OrderActorRole AddedByActorRole { get; init; }

    public decimal CurrentUnitPriceAmount { get; set; }
    public bool IsFulfilled { get; set; }
    public bool IsRemoved { get; set; }
}
