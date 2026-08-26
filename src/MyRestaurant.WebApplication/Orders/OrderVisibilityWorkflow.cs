using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Orders;

public interface IOrderVisibilityWorkflow
{
    Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class OrderVisibilityWorkflow : IOrderVisibilityWorkflow
{
    private readonly IOrderVisibility _visibility;
    private readonly IDomainEventBroadcaster _broadcaster;

    public OrderVisibilityWorkflow(
        IOrderVisibility visibility,
        IDomainEventBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(broadcaster);

        _visibility = visibility;
        _broadcaster = broadcaster;
    }

    public async Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        HideOrderResult result = await _visibility
            .HideAsync(guestOrderIdentifier, ownerPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsHidden)
        {
            _broadcaster.Publish(new VisibilityChanged(result.GuestOrderIdentifier));
        }

        return result;
    }

    public async Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        UnhideOrderResult result = await _visibility
            .UnhideAsync(guestOrderIdentifier, administratorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsUnhidden)
        {
            _broadcaster.Publish(new VisibilityChanged(result.GuestOrderIdentifier));
        }

        return result;
    }
}
