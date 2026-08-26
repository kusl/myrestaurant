namespace MyRestaurant.Domain.LiveUpdates;

public interface IDomainEventBroadcaster
{
    void Publish(DomainNotification notification);

    IDisposable Subscribe(Action<DomainNotification> handler);
}
