namespace MyRestaurant.Domain.Orders;

/// <summary>
/// The kind of an <see cref="OrderEvent"/> (TECHNICAL_SPECIFICATION §6.2). The database
/// binds each type to the roles allowed to author it via same-row CHECKs (§8.2); the domain
/// mirrors those rules in <c>OrderMutationValidator</c>.
/// </summary>
public enum OrderEventType
{
    GuestSubmission,
    StaffEdit,
    PriceAdjustment,
    Fulfillment,
    FulfillmentReversal,
}

/// <summary>
/// The capacity in which an actor authored an event (TECHNICAL_SPECIFICATION §6.2).
/// <c>guest</c> is not a stored role — it is the implicit capacity of any person acting on
/// their own order (an administrator dining acts as <see cref="Guest"/>).
/// </summary>
public enum OrderActorRole
{
    Guest,
    Kitchen,
    Counter,
    Administrator,
}

/// <summary>
/// One typed operation owned by an event (TECHNICAL_SPECIFICATION §6.3). Concrete subtypes
/// map one-to-one to the typed operation tables; there are no JSON payloads and no
/// entity-attribute-value (ADR-0002).
/// </summary>
public abstract record OrderOperation;

/// <summary>A new line: its own identity, the item, quantity, the price captured at add time, and an optional note.</summary>
public sealed record LineAddedOperation(
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    int Quantity,
    decimal UnitPriceAmount,
    string? CustomizationNote) : OrderOperation;

/// <summary>Terminal removal of a line (guests may remove only their own pending lines, §6.4).</summary>
public sealed record LineRemovedOperation(Guid OrderLineIdentifier, string? Reason) : OrderOperation;

/// <summary>A price adjustment carrying a required, non-empty reason (§6.4, §6.5.7).</summary>
public sealed record LinePriceAdjustedOperation(Guid OrderLineIdentifier, decimal NewUnitPriceAmount, string Reason) : OrderOperation;

/// <summary>Kitchen marks a pending line prepared and dispatched.</summary>
public sealed record LineFulfilledOperation(Guid OrderLineIdentifier) : OrderOperation;

/// <summary>Kitchen reverses a fulfillment (roll-forward; the line returns to pending).</summary>
public sealed record LineFulfillmentRevertedOperation(Guid OrderLineIdentifier) : OrderOperation;

/// <summary>
/// One append-only order event with its operations (TECHNICAL_SPECIFICATION §6.2). The
/// sequence number is monotonic per order (assigned under the order lock, §6.6); the event
/// tables are the source of truth — projections and the fold are derived reads (§8.5).
/// </summary>
public sealed record OrderEvent(
    Guid OrderEventIdentifier,
    Guid GuestOrderIdentifier,
    long SequenceNumber,
    OrderEventType EventType,
    Guid ActorPersonIdentifier,
    OrderActorRole ActorRole,
    DateTimeOffset OccurredAt,
    IReadOnlyList<OrderOperation> Operations);

/// <summary>Helpers relating event types, roles, and operations (mirrors the §8.2 CHECKs).</summary>
public static class OrderEventRules
{
    /// <summary>The line identifier every operation carries.</summary>
    public static Guid LineIdentifierOf(OrderOperation operation) => operation switch
    {
        LineAddedOperation added => added.OrderLineIdentifier,
        LineRemovedOperation removed => removed.OrderLineIdentifier,
        LinePriceAdjustedOperation adjusted => adjusted.OrderLineIdentifier,
        LineFulfilledOperation fulfilled => fulfilled.OrderLineIdentifier,
        LineFulfillmentRevertedOperation reverted => reverted.OrderLineIdentifier,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.GetType().Name, "Unknown operation type."),
    };

    /// <summary>Whether an operation subtype is permitted inside an event of the given type (§6.3).</summary>
    public static bool OperationIsAllowedFor(OrderEventType eventType, OrderOperation operation) => operation switch
    {
        LineAddedOperation or LineRemovedOperation => eventType is OrderEventType.GuestSubmission or OrderEventType.StaffEdit,
        LinePriceAdjustedOperation => eventType is OrderEventType.PriceAdjustment,
        LineFulfilledOperation => eventType is OrderEventType.Fulfillment,
        LineFulfillmentRevertedOperation => eventType is OrderEventType.FulfillmentReversal,
        _ => false,
    };

    /// <summary>Whether a role may author an event of the given type (§6.2 same-row CHECKs).</summary>
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
