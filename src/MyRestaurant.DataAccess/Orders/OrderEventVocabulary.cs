using MyRestaurant.Domain.Orders;

namespace MyRestaurant.DataAccess.Orders;

internal static class OrderEventVocabulary
{
    public const string GuestSubmission = "guest_submission";
    public const string StaffEdit = "staff_edit";
    public const string PriceAdjustment = "price_adjustment";
    public const string Fulfillment = "fulfillment";
    public const string FulfillmentReversal = "fulfillment_reversal";

    public const string Guest = "guest";
    public const string Kitchen = "kitchen";
    public const string Counter = "counter";
    public const string Administrator = "administrator";

    public const string InitialNotification = "initial";
    public const string ReminderNotification = "reminder";

    public const string HiddenVisibility = "hidden";
    public const string UnhiddenVisibility = "unhidden";

    public const string LineAddedKind = "line_added";
    public const string LineRemovedKind = "line_removed";
    public const string LinePriceAdjustedKind = "line_price_adjusted";
    public const string LineFulfilledKind = "line_fulfilled";
    public const string LineFulfillmentRevertedKind = "line_fulfillment_reverted";

    public static string ToDatabase(OrderEventType eventType) => eventType switch
    {
        OrderEventType.GuestSubmission => GuestSubmission,
        OrderEventType.StaffEdit => StaffEdit,
        OrderEventType.PriceAdjustment => PriceAdjustment,
        OrderEventType.Fulfillment => Fulfillment,
        OrderEventType.FulfillmentReversal => FulfillmentReversal,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown order event type."),
    };

    public static OrderEventType EventTypeFrom(string stored) => stored switch
    {
        GuestSubmission => OrderEventType.GuestSubmission,
        StaffEdit => OrderEventType.StaffEdit,
        PriceAdjustment => OrderEventType.PriceAdjustment,
        Fulfillment => OrderEventType.Fulfillment,
        FulfillmentReversal => OrderEventType.FulfillmentReversal,
        _ => throw new InvalidOperationException($"Unknown stored order event type '{stored}'."),
    };

    public static string ToDatabase(OrderActorRole actorRole) => actorRole switch
    {
        OrderActorRole.Guest => Guest,
        OrderActorRole.Kitchen => Kitchen,
        OrderActorRole.Counter => Counter,
        OrderActorRole.Administrator => Administrator,
        _ => throw new ArgumentOutOfRangeException(nameof(actorRole), actorRole, "Unknown order actor role."),
    };

    public static OrderActorRole ActorRoleFrom(string stored) => stored switch
    {
        Guest => OrderActorRole.Guest,
        Kitchen => OrderActorRole.Kitchen,
        Counter => OrderActorRole.Counter,
        Administrator => OrderActorRole.Administrator,
        _ => throw new InvalidOperationException($"Unknown stored order actor role '{stored}'."),
    };
}
