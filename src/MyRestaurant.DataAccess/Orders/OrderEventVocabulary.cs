using MyRestaurant.Domain.Orders;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>
/// The one place the §6.2 enum values are spelled as the strings the database CHECKs accept
/// (TECHNICAL_SPECIFICATION §6.2, §6.3, §8.2). Both directions live together on purpose: a writer and a
/// reader that disagree about a single word produce a constraint violation on write and a silent
/// mis-fold on read, and neither is caught by a compiler. Everything else in
/// <c>MyRestaurant.DataAccess.Orders</c> goes through here rather than embedding a literal.
///
/// <para>An unrecognised stored value throws rather than defaulting: the columns are CHECK-constrained,
/// so an unknown word means the schema and this file have diverged, and a fold that quietly treats an
/// unknown event as a no-op would hand a guest a wrong bill.</para>
/// </summary>
internal static class OrderEventVocabulary
{
    // order_event.event_type (§8.2).
    public const string GuestSubmission = "guest_submission";
    public const string StaffEdit = "staff_edit";
    public const string PriceAdjustment = "price_adjustment";
    public const string Fulfillment = "fulfillment";
    public const string FulfillmentReversal = "fulfillment_reversal";

    // order_event.actor_role (§8.2). `guest` is a capacity, not a stored person_role (§0, §3.7).
    public const string Guest = "guest";
    public const string Kitchen = "kitchen";
    public const string Counter = "counter";
    public const string Administrator = "administrator";

    // kitchen_notification.kind (§8.2, §10).
    public const string InitialNotification = "initial";
    public const string ReminderNotification = "reminder";

    // order_visibility_event.event_type (§6.8, §8.2). A second closed two-word vocabulary, kept here
    // rather than in its own file for the reason this file exists at all: the writer and the reader of a
    // CHECK-constrained column must agree on the spelling, and a disagreement is a constraint violation
    // on one side and a silent mis-read on the other. §6.8 calls the administrator's unhide
    // "unhidden_by_administrator" in prose; the stored word is `unhidden`, and who did it is
    // `actor_person_identifier` — there is no guest unhide path to distinguish it from (§6.8: "there is
    // no user-facing unhide").
    public const string HiddenVisibility = "hidden";
    public const string UnhiddenVisibility = "unhidden";

    // Discriminators for the UNION ALL that reads the five typed operation tables as one flat set.
    // They are query-local labels, not stored anywhere, but they live here for the same reason.
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
