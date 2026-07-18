namespace MyRestaurant.Domain.LiveUpdates;

/// <summary>
/// A post-commit live-update notification (TECHNICAL_SPECIFICATION §9). Payloads are
/// identifiers, not state: a subscriber re-queries the projection views scoped by the ids it
/// receives. This keeps the broadcaster a thin fan-out and the read model the single place
/// state is shaped.
/// </summary>
public abstract record DomainNotification;

/// <summary>Which kitchen alert fired (TECHNICAL_SPECIFICATION §10).</summary>
public enum KitchenAlertKind
{
    /// <summary>A new guest send, or a counter/administrator line-changing staff edit (§10.1).</summary>
    Initial,

    /// <summary>The single follow-up if a send's added lines sit untouched past the threshold (§10.2).</summary>
    Reminder,
}

/// <summary>Any order-event commit — table members of the sitting and the counter re-query.</summary>
public sealed record OrderLinesChanged(Guid SittingIdentifier, Guid GuestOrderIdentifier) : DomainNotification;

/// <summary>A kitchen_notification row was written (initial or reminder) — the kitchen alerts.</summary>
public sealed record KitchenAlert(Guid OrderEventIdentifier, KitchenAlertKind Kind) : DomainNotification;

/// <summary>A fulfillment or reversal commit — table members and the kitchen re-query.</summary>
public sealed record LineFulfillmentChanged(Guid SittingIdentifier, Guid GuestOrderIdentifier) : DomainNotification;

/// <summary>A menu item or menu_item_event commit — every surface showing the menu re-queries.</summary>
public sealed record MenuChanged : DomainNotification;

/// <summary>A membership insert — table members and the table display (party size) re-query.</summary>
public sealed record SittingMemberJoined(Guid SittingIdentifier) : DomainNotification;

/// <summary>A close commit — table members, kitchen, and counter re-query; the table flips to settled.</summary>
public sealed record SittingClosed(Guid SittingIdentifier) : DomainNotification;

/// <summary>A visibility (hide/unhide) commit — the affected table members' history views re-query.</summary>
public sealed record VisibilityChanged(Guid GuestOrderIdentifier) : DomainNotification;
