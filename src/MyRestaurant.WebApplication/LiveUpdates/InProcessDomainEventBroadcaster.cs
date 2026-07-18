using System.Collections.Concurrent;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.LiveUpdates;

/// <summary>
/// The single v1 implementation of <see cref="IDomainEventBroadcaster"/> (TECHNICAL_SPECIFICATION §9,
/// ADR-0006 "no Redis"): an in-process fan-out to every subscribed Blazor circuit. Registered as a
/// singleton. If a future second web replica ever forces a backplane, this class is replaced and
/// nothing else changes.
///
/// Guarantees:
/// <list type="bullet">
///   <item><see cref="Publish"/> never throws to the caller — a domain commit must not be undone by a
///   misbehaving subscriber. Each handler is invoked in its own try/catch and failures are logged.</item>
///   <item>Subscription and unsubscription are safe under concurrency (a
///   <see cref="ConcurrentDictionary{TKey,TValue}"/>), and publishing iterates a snapshot so a handler
///   may subscribe or dispose during dispatch without corrupting the loop.</item>
/// </list>
/// </summary>
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

        // Snapshot Values so a handler that subscribes/unsubscribes mid-dispatch cannot break iteration.
        foreach (Action<DomainNotification> handler in _handlers.Values)
        {
            try
            {
                handler(notification);
            }
            catch (Exception exception)
            {
                // A single bad subscriber must never fail the publisher (the committed domain change stands).
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

    /// <summary>The disposal token returned from <see cref="Subscribe"/>; removes the handler, idempotently.</summary>
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
