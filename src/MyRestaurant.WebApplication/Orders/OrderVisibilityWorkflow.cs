using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// The web layer's entry point for §6.8's two visibility writes (TECHNICAL_SPECIFICATION §6.8, §9,
/// §11.1, §11.4).
///
/// <para>Same division of labour as <see cref="IOrderWorkflow"/>, <see cref="Menu.IMenuWorkflow"/>, and
/// <see cref="Sittings.ISittingWorkflow"/>: <see cref="IOrderVisibility"/> owns the transaction and stops
/// at commit, because a data-access service has no business knowing about Blazor circuits. The one thing
/// that must happen after that commit happens here — §9's <c>VisibilityChanged(orderId)</c> goes out to
/// every subscribed circuit.</para>
///
/// <para><b>No metric.</b> §12's meter list is closed and contains no visibility counter, correctly: the
/// numbers there are the ones an operator watches a service by — sends, lines, reminders, closes, token
/// validations, sign-ins — and how often guests tidy their own history is not one of them. The event log
/// is the record of who hid what, and it is complete.</para>
///
/// <para>A surface calls this and never <see cref="IOrderVisibility"/> directly. The broadcast is not
/// cosmetic here either: §9 routes <c>VisibilityChanged</c> to "table members (history views)", and a
/// guest with their history open on one phone and the order surface on another would otherwise keep
/// seeing a row they had just asked to have gone.</para>
/// </summary>
public interface IOrderVisibilityWorkflow
{
    /// <summary>
    /// §6.8's owner hide, then the announcement. Refusals — not the owner, sitting still open, already
    /// hidden, no such order — publish nothing, because nothing changed.
    /// </summary>
    Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// §6.8's administrator unhide, then the announcement. A refusal publishes nothing, on the same
    /// terms.
    /// </summary>
    Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only implementation of <see cref="IOrderVisibilityWorkflow"/>: a thin post-commit shell over
/// <see cref="IOrderVisibility"/>.
/// </summary>
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

        // Only the call that appended the row. AlreadyHidden wrote nothing and was already announced by
        // whoever did write it; announcing it again would make every subscriber re-query for a change
        // that happened before this request arrived.
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
