using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Menu;

/// <summary>
/// The web layer's entry point for changing the menu (TECHNICAL_SPECIFICATION §7, §9, §11.2).
///
/// <para>Same division of labour as <see cref="Orders.IOrderWorkflow"/>: <see cref="IMenuAvailability"/>
/// owns the transaction and stops at commit, and the §9 broadcast happens here, after it. A surface
/// calls this and never <see cref="IMenuAvailability"/> directly — otherwise a guest with the table page
/// open would keep an 86'd item selectable in their picker until they happened to reload, and would then
/// have a whole send refused for it (§6.5.9).</para>
///
/// <para>Only availability today. When M5 brings the rest of §11.4's menu CRUD — create, rename,
/// reprice, and the per-item event history — it grows methods here rather than a second workflow, since
/// every one of them publishes the same <see cref="MenuChanged"/> to the same subscribers.</para>
/// </summary>
public interface IMenuWorkflow
{
    /// <summary>
    /// Flips one item's availability (§7's "86") and, if the flag actually moved, announces it so every
    /// open menu re-reads itself.
    /// </summary>
    Task<SetMenuItemAvailabilityResult> SetMenuItemActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only implementation of <see cref="IMenuWorkflow"/>: a thin post-commit shell over
/// <see cref="IMenuAvailability"/>.
/// </summary>
public sealed class MenuAvailabilityWorkflow : IMenuWorkflow
{
    private readonly IMenuAvailability _availability;
    private readonly IDomainEventBroadcaster _broadcaster;

    public MenuAvailabilityWorkflow(
        IMenuAvailability availability,
        IDomainEventBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(broadcaster);

        _availability = availability;
        _broadcaster = broadcaster;
    }

    public async Task<SetMenuItemAvailabilityResult> SetMenuItemActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        SetMenuItemAvailabilityResult result = await _availability
            .SetActiveAsync(menuItemIdentifier, isActive, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // §9: MenuChanged fires on "a menu item or menu_item_event commit". A no-op flip committed
        // nothing, so announcing it would make every open surface re-query for no reason.
        if (result.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }
}
