using MyRestaurant.Domain.Orders;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Terse constructors for order events and operations, so the projection and validator tests read as
/// scenarios rather than positional-argument noise. Event identifiers are random (identity only);
/// callers that need to reference the adding event capture the returned <see cref="OrderEvent"/>.
/// </summary>
internal static class OrderTestBuilders
{
    public static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(int secondsAfterOrigin) => Origin.AddSeconds(secondsAfterOrigin);

    public static OrderEvent Event(
        Guid orderIdentifier,
        long sequenceNumber,
        OrderEventType eventType,
        Guid actor,
        OrderActorRole role,
        DateTimeOffset occurredAt,
        params OrderOperation[] operations)
        => new(Guid.NewGuid(), orderIdentifier, sequenceNumber, eventType, actor, role, occurredAt, operations);

    public static OrderEvent GuestSubmission(
        Guid orderIdentifier, long sequenceNumber, Guid actor, DateTimeOffset occurredAt, params OrderOperation[] operations)
        => Event(orderIdentifier, sequenceNumber, OrderEventType.GuestSubmission, actor, OrderActorRole.Guest, occurredAt, operations);

    public static OrderEvent StaffEdit(
        Guid orderIdentifier, long sequenceNumber, Guid actor, OrderActorRole role, DateTimeOffset occurredAt, params OrderOperation[] operations)
        => Event(orderIdentifier, sequenceNumber, OrderEventType.StaffEdit, actor, role, occurredAt, operations);

    public static OrderEvent PriceAdjustment(
        Guid orderIdentifier, long sequenceNumber, Guid actor, OrderActorRole role, DateTimeOffset occurredAt, params OrderOperation[] operations)
        => Event(orderIdentifier, sequenceNumber, OrderEventType.PriceAdjustment, actor, role, occurredAt, operations);

    public static OrderEvent Fulfillment(
        Guid orderIdentifier, long sequenceNumber, Guid actor, OrderActorRole role, DateTimeOffset occurredAt, params OrderOperation[] operations)
        => Event(orderIdentifier, sequenceNumber, OrderEventType.Fulfillment, actor, role, occurredAt, operations);

    public static OrderEvent FulfillmentReversal(
        Guid orderIdentifier, long sequenceNumber, Guid actor, OrderActorRole role, DateTimeOffset occurredAt, params OrderOperation[] operations)
        => Event(orderIdentifier, sequenceNumber, OrderEventType.FulfillmentReversal, actor, role, occurredAt, operations);

    public static LineAddedOperation Add(Guid lineIdentifier, Guid menuItemIdentifier, int quantity, decimal unitPrice, string? note = null)
        => new(lineIdentifier, menuItemIdentifier, quantity, unitPrice, note);

    public static LineRemovedOperation Remove(Guid lineIdentifier, string? reason = null)
        => new(lineIdentifier, reason);

    public static LinePriceAdjustedOperation AdjustPrice(Guid lineIdentifier, decimal newUnitPrice, string reason)
        => new(lineIdentifier, newUnitPrice, reason);

    public static LineFulfilledOperation Fulfill(Guid lineIdentifier) => new(lineIdentifier);

    public static LineFulfillmentRevertedOperation Revert(Guid lineIdentifier) => new(lineIdentifier);
}
