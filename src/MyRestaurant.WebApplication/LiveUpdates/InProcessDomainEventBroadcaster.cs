using System.Collections.Concurrent;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.LiveUpdates;

public sealed class InProcessDomainEventBroadcaster : IDomainEventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Action<DomainNotification>> _handlers = new();
    private readonly ILogger<InProcessDomainEventBroadcaster> _logger;

    public InProcessDomainEventBroadcaster(ILogger<InProcessDomainEventBroadcaster> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Publish(DomainNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        foreach (Action<DomainNotification> handler in _handlers.Values)
        {
            try
            {
                handler(notification);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "A live-update subscriber threw while handling {NotificationType}; it was isolated.",
                    notification.GetType().Name);
            }
        }
    }

    public IDisposable Subscribe(Action<DomainNotification> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        Guid token = Guid.NewGuid();
        _handlers[token] = handler;
        return new Subscription(this, token);
    }

    private void Unsubscribe(Guid token) => _handlers.TryRemove(token, out _);

    private sealed class Subscription : IDisposable
    {
        private readonly InProcessDomainEventBroadcaster _owner;
        private readonly Guid _token;
        private bool _disposed;

        public Subscription(InProcessDomainEventBroadcaster owner, Guid token)
        {
            _owner = owner;
            _token = token;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Unsubscribe(_token);
        }
    }
}
