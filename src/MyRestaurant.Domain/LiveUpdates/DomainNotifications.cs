namespace MyRestaurant.Domain.LiveUpdates;

public abstract record DomainNotification;

public enum KitchenAlertKind
{
    Initial,
    Reminder,
}

public sealed record OrderLinesChanged(Guid SittingIdentifier, Guid GuestOrderIdentifier) : DomainNotification;

public sealed record KitchenAlert(Guid OrderEventIdentifier, KitchenAlertKind Kind) : DomainNotification;

public sealed record LineFulfillmentChanged(Guid SittingIdentifier, Guid GuestOrderIdentifier) : DomainNotification;

public sealed record MenuChanged : DomainNotification;

public sealed record SittingMemberJoined(Guid SittingIdentifier) : DomainNotification;

public sealed record SittingClosed(Guid SittingIdentifier) : DomainNotification;

public sealed record VisibilityChanged(Guid GuestOrderIdentifier) : DomainNotification;
