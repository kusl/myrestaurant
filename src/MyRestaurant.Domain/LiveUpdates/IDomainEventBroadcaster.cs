namespace MyRestaurant.Domain.LiveUpdates;

/// <summary>
/// Fans out <see cref="DomainNotification"/>s to subscribed Blazor circuits <em>after commit</em>
/// (TECHNICAL_SPECIFICATION §9). The interface lives in the domain; the only v1 implementation is
/// in-process, in the web layer (no Redis — ADR-0006). If a second web replica ever forces a
/// backplane, only the implementation changes, never this contract or its callers.
/// </summary>
public interface IDomainEventBroadcaster
{
    /// <summary>Publish a notification to all current subscribers. Never throws to the caller.</summary>
    void Publish(DomainNotification notification);

    /// <summary>Subscribe a handler; dispose the returned token to unsubscribe (components do this on disposal).</summary>
    IDisposable Subscribe(Action<DomainNotification> handler);
}
