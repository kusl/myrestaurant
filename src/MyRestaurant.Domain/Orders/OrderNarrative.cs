namespace MyRestaurant.Domain.Orders;

public sealed record NarratedPriceAdjustment(
    decimal PreviousUnitPriceAmount,
    decimal NewUnitPriceAmount,
    string Reason,
    OrderActorRole ActorRole,
    DateTimeOffset OccurredAt);

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
    public decimal LineTotalAmount => IsRemoved ? 0m : Quantity * CurrentUnitPriceAmount;

    public bool IsPriceAdjusted => PriceAdjustments.Count > 0;

    public bool IsPending => !IsRemoved && !IsFulfilled;

    public bool GuestMayRemove(Guid personIdentifier)
        => IsPending
        && AddedByEventType == OrderEventType.GuestSubmission
        && AddedByActorPersonIdentifier == personIdentifier;
}

public static class OrderNarrative
{
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
