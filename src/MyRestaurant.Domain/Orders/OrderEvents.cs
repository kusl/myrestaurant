namespace MyRestaurant.Domain.Orders;

public enum OrderEventType
{
    GuestSubmission,
    StaffEdit,
    PriceAdjustment,
    Fulfillment,
    FulfillmentReversal,
}

public enum OrderActorRole
{
    Guest,
    Kitchen,
    Counter,
    Administrator,
}

public closed record class OrderOperation;

public sealed record LineAddedOperation(
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    int Quantity,
    decimal UnitPriceAmount,
    string? CustomizationNote) : OrderOperation;

public sealed record LineRemovedOperation(Guid OrderLineIdentifier, string? Reason) : OrderOperation;

public sealed record LinePriceAdjustedOperation(Guid OrderLineIdentifier, decimal NewUnitPriceAmount, string Reason) : OrderOperation;

public sealed record LineFulfilledOperation(Guid OrderLineIdentifier) : OrderOperation;

public sealed record LineFulfillmentRevertedOperation(Guid OrderLineIdentifier) : OrderOperation;

public sealed record OrderEvent(
    Guid OrderEventIdentifier,
    Guid GuestOrderIdentifier,
    long SequenceNumber,
    OrderEventType EventType,
    Guid ActorPersonIdentifier,
    OrderActorRole ActorRole,
    DateTimeOffset OccurredAt,
    IReadOnlyList<OrderOperation> Operations);

public static class OrderEventRules
{
    public static Guid LineIdentifierOf(OrderOperation operation) => operation switch
    {
        LineAddedOperation added => added.OrderLineIdentifier,
        LineRemovedOperation removed => removed.OrderLineIdentifier,
        LinePriceAdjustedOperation adjusted => adjusted.OrderLineIdentifier,
        LineFulfilledOperation fulfilled => fulfilled.OrderLineIdentifier,
        LineFulfillmentRevertedOperation reverted => reverted.OrderLineIdentifier,
    };

    public static bool OperationIsAllowedFor(OrderEventType eventType, OrderOperation operation) => operation switch
    {
        LineAddedOperation or LineRemovedOperation => eventType is OrderEventType.GuestSubmission or OrderEventType.StaffEdit,
        LinePriceAdjustedOperation => eventType is OrderEventType.PriceAdjustment,
        LineFulfilledOperation => eventType is OrderEventType.Fulfillment,
        LineFulfillmentRevertedOperation => eventType is OrderEventType.FulfillmentReversal,
    };

    public static bool RoleMayAuthor(OrderEventType eventType, OrderActorRole role) => eventType switch
    {
        OrderEventType.GuestSubmission => role is OrderActorRole.Guest,
        OrderEventType.StaffEdit => role is OrderActorRole.Kitchen or OrderActorRole.Counter or OrderActorRole.Administrator,
        OrderEventType.PriceAdjustment => role is OrderActorRole.Counter or OrderActorRole.Administrator,
        OrderEventType.Fulfillment => role is OrderActorRole.Kitchen or OrderActorRole.Administrator,
        OrderEventType.FulfillmentReversal => role is OrderActorRole.Kitchen or OrderActorRole.Administrator,
        _ => false,
    };
}
