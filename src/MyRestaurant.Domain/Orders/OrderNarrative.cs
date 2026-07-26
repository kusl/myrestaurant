namespace MyRestaurant.Domain.Orders;

/// <summary>
/// One price adjustment as a reader sees it (TECHNICAL_SPECIFICATION §6.3, §11.1 — "price adjustments
/// shown old → new with reason").
///
/// <para><see cref="PreviousUnitPriceAmount"/> is not stored anywhere: the operation table records only
/// the new price, so "old" is whatever the line's price was at the moment this adjustment was folded in.
/// That is exactly why this lives in the fold rather than in a view — the SQL projections answer "what
/// is the price now", and the arrow needs the step before it.</para>
/// </summary>
/// <param name="PreviousUnitPriceAmount">The unit price this adjustment replaced.</param>
/// <param name="NewUnitPriceAmount">The unit price it set.</param>
/// <param name="Reason">The required, non-empty reason (§6.5.7).</param>
/// <param name="ActorRole">Counter or administrator (§6.2 binds the type to those two roles).</param>
/// <param name="OccurredAt">When the adjusting event was stamped.</param>
public sealed record NarratedPriceAdjustment(
    decimal PreviousUnitPriceAmount,
    decimal NewUnitPriceAmount,
    string Reason,
    OrderActorRole ActorRole,
    DateTimeOffset OccurredAt);

/// <summary>
/// One line's whole story, folded from the order's event log (TECHNICAL_SPECIFICATION §6.4, §11.1).
///
/// <para>This is deliberately <em>not</em> the same thing as <see cref="ProjectedOrderLine"/> or the
/// <c>order_current_line</c> view. Those answer "what is on the table right now" and drop removed lines
/// on the floor, which is the correct answer for a bill and the wrong one for a guest: §11.1 asks for
/// "removed lines struck-through with actor + reason, price adjustments shown old → new with reason".
/// An append-only log exists so history survives state (ADR-0002), and this record is where that
/// history reaches a screen.</para>
///
/// <para>The two are held in step by construction: <see cref="OrderNarrative.FromEvents"/> folds the
/// same events in the same order with the same "latest by sequence number wins" rule, and a domain test
/// asserts that its non-removed lines equal <see cref="OrderProjection.FromEvents"/>'s lines on
/// randomised sequences — the §8.5 equivalence argument extended one link further.</para>
/// </summary>
public sealed record NarratedOrderLine(
    Guid GuestOrderIdentifier,
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    int Quantity,
    decimal OriginalUnitPriceAmount,
    decimal CurrentUnitPriceAmount,
    string? CustomizationNote,
    bool IsFulfilled,
    DateTimeOffset AddedAt,
    Guid AddedByOrderEventIdentifier,
    OrderEventType AddedByEventType,
    Guid AddedByActorPersonIdentifier,
    OrderActorRole AddedByActorRole,
    bool IsRemoved,
    Guid? RemovedByActorPersonIdentifier,
    OrderActorRole? RemovedByActorRole,
    string? RemovalReason,
    DateTimeOffset? RemovedAt,
    IReadOnlyList<NarratedPriceAdjustment> PriceAdjustments)
{
    /// <summary>Extended line price at the current unit price. Zero once removed — a removed line bills nothing.</summary>
    public decimal LineTotalAmount => IsRemoved ? 0m : Quantity * CurrentUnitPriceAmount;

    /// <summary>True when at least one <c>price_adjustment</c> has touched this line (§6.3).</summary>
    public bool IsPriceAdjusted => PriceAdjustments.Count > 0;

    /// <summary>True while the line is on the table and waiting: not removed, not yet fulfilled (§6.4).</summary>
    public bool IsPending => !IsRemoved && !IsFulfilled;

    /// <summary>
    /// Whether <paramref name="personIdentifier"/> may mark this line for removal in a
    /// <c>guest_submission</c> — §6.5.3's rule stated once, on the read side, so the surface can grey
    /// out what the transaction would refuse instead of offering a control that always fails. The
    /// transaction re-decides it under the lock regardless; this is courtesy, not enforcement.
    /// </summary>
    public bool GuestMayRemove(Guid personIdentifier)
        => IsPending
        && AddedByEventType == OrderEventType.GuestSubmission
        && AddedByActorPersonIdentifier == personIdentifier;
}

/// <summary>
/// The pure fold from an order's event log to the per-line narrative §11.1 renders
/// (TECHNICAL_SPECIFICATION §6.4, §8.5, §11.1). A sibling of <see cref="OrderProjection"/>, not a
/// replacement: that one answers the question the bill asks, this one answers the question the guest
/// asks. Neither is the source of truth — the event tables are (ADR-0002).
/// </summary>
public static class OrderNarrative
{
    /// <summary>
    /// Every line the order has ever had, removed ones included, ordered the way
    /// <see cref="OrderProjection.FromEvents"/> orders its lines: by the moment the line was added, then
    /// by line identifier. Events are folded in ascending <c>sequence_number</c>, so "latest by sequence
    /// wins" for prices and fulfillment flips, matching the LATERAL sub-selects of
    /// <c>order_current_line</c> (§8.3).
    /// </summary>
    public static IReadOnlyList<NarratedOrderLine> FromEvents(IReadOnlyList<OrderEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        Dictionary<Guid, Accumulator> lines = new();

        foreach (OrderEvent orderEvent in events.OrderBy(orderEvent => orderEvent.SequenceNumber))
        {
            foreach (OrderOperation operation in orderEvent.Operations)
            {
                switch (operation)
                {
                    // A repeat add for a line identifier already known is ignored rather than
                    // overwriting: the identifier IS the line's identity and the schema's UNIQUE
                    // constraint makes it impossible, but a fold must not depend on that to be total.
                    case LineAddedOperation added when !lines.ContainsKey(added.OrderLineIdentifier):
                        lines[added.OrderLineIdentifier] = new Accumulator
                        {
                            GuestOrderIdentifier = orderEvent.GuestOrderIdentifier,
                            OrderLineIdentifier = added.OrderLineIdentifier,
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

                    case LineRemovedOperation removed
                        when lines.TryGetValue(removed.OrderLineIdentifier, out Accumulator? removedLine):
                        removedLine.IsRemoved = true;
                        removedLine.RemovedByActorPersonIdentifier = orderEvent.ActorPersonIdentifier;
                        removedLine.RemovedByActorRole = orderEvent.ActorRole;
                        removedLine.RemovalReason = removed.Reason;
                        removedLine.RemovedAt = orderEvent.OccurredAt;
                        break;

                    case LinePriceAdjustedOperation adjusted
                        when lines.TryGetValue(adjusted.OrderLineIdentifier, out Accumulator? adjustedLine):
                        adjustedLine.PriceAdjustments.Add(new NarratedPriceAdjustment(
                            adjustedLine.CurrentUnitPriceAmount,
                            adjusted.NewUnitPriceAmount,
                            adjusted.Reason,
                            orderEvent.ActorRole,
                            orderEvent.OccurredAt));
                        adjustedLine.CurrentUnitPriceAmount = adjusted.NewUnitPriceAmount;
                        break;

                    case LineFulfilledOperation fulfilled
                        when lines.TryGetValue(fulfilled.OrderLineIdentifier, out Accumulator? fulfilledLine):
                        fulfilledLine.IsFulfilled = true;
                        break;

                    case LineFulfillmentRevertedOperation reverted
                        when lines.TryGetValue(reverted.OrderLineIdentifier, out Accumulator? revertedLine):
                        revertedLine.IsFulfilled = false;
                        break;
                }
            }
        }

        return lines.Values
            .Select(line => line.ToNarratedLine())
            .OrderBy(line => line.AddedAt)
            .ThenBy(line => line.OrderLineIdentifier)
            .ToArray();
    }

    /// <summary>Mutable per-line accumulator used only inside the fold.</summary>
    private sealed class Accumulator
    {
        public required Guid GuestOrderIdentifier { get; init; }
        public required Guid OrderLineIdentifier { get; init; }
        public required Guid MenuItemIdentifier { get; init; }
        public required int Quantity { get; init; }
        public required decimal OriginalUnitPriceAmount { get; init; }
        public required string? CustomizationNote { get; init; }
        public required DateTimeOffset AddedAt { get; init; }
        public required Guid AddedByOrderEventIdentifier { get; init; }
        public required OrderEventType AddedByEventType { get; init; }
        public required Guid AddedByActorPersonIdentifier { get; init; }
        public required OrderActorRole AddedByActorRole { get; init; }

        public required decimal CurrentUnitPriceAmount { get; set; }
        public bool IsFulfilled { get; set; }
        public bool IsRemoved { get; set; }
        public Guid? RemovedByActorPersonIdentifier { get; set; }
        public OrderActorRole? RemovedByActorRole { get; set; }
        public string? RemovalReason { get; set; }
        public DateTimeOffset? RemovedAt { get; set; }
        public List<NarratedPriceAdjustment> PriceAdjustments { get; } = [];

        public NarratedOrderLine ToNarratedLine() => new(
            GuestOrderIdentifier,
            OrderLineIdentifier,
            MenuItemIdentifier,
            Quantity,
            OriginalUnitPriceAmount,
            CurrentUnitPriceAmount,
            CustomizationNote,
            IsFulfilled,
            AddedAt,
            AddedByOrderEventIdentifier,
            AddedByEventType,
            AddedByActorPersonIdentifier,
            AddedByActorRole,
            IsRemoved,
            RemovedByActorPersonIdentifier,
            RemovedByActorRole,
            RemovalReason,
            RemovedAt,
            PriceAdjustments.ToArray());
    }
}
