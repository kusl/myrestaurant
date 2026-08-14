using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Menu;

/// <summary>
/// The web layer's entry point for changing the menu (TECHNICAL_SPECIFICATION §7, §9, §11.2, §11.4).
///
/// <para>Same division of labour as <see cref="Orders.IOrderWorkflow"/>: the data-access services own
/// the transaction and stop at commit, and the §9 broadcast happens here, after it. A surface calls this
/// and never <see cref="IMenuAvailability"/> or <see cref="IMenuAdministration"/> directly — otherwise a
/// guest with the table page open would keep a stale menu until they happened to reload, and would then
/// have a whole send refused for an 86'd item (§6.5.9) or be quoted a price nobody charges any
/// more.</para>
///
/// <para>One workflow over two write services, because there is one notification. §9 fires
/// <see cref="MenuChanged"/> on "a menu item or <c>menu_item_event</c> commit" without distinguishing
/// which verb caused it, and every subscriber responds the same way: re-read the menu. Splitting this in
/// two would make it possible to wire an application that announces 86s and not repricings.</para>
///
/// <para><b>Not every call publishes.</b> A rename to the name it already has, a reprice to the price it
/// already has, a description equal to the stored one, a move to the position it is already at, and a
/// toggle to the state it is already in all commit nothing, and announcing them would tell every open
/// surface in the building to re-query for a change that did not happen. The write services report that
/// distinction; this file's whole job is to honour it.</para>
/// </summary>
public interface IMenuWorkflow
{
    /// <summary>
    /// Creates a menu item (§11.4) and announces it. A create always commits, so this always publishes.
    ///
    /// <para>The description travels with it and is stored on the row; when it is non-blank the write
    /// service also appends a <c>description_changed</c> event in the same transaction, because §8.2's
    /// <c>created</c> carries the name and the price only. That is the write service's business, not this
    /// file's — one commit, one announcement, whether it wrote one event or two.</para>
    /// </summary>
    Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>Renames one item (§7) and, if the name actually moved, announces it.</summary>
    Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reprices one item (§7) and, if the price actually moved, announces it. Lines already on a bill
    /// keep the price captured when they were added (§6.5.4); the broadcast is so the pickers quote the
    /// new one.
    /// </summary>
    Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears one item's description (§7) and, if it actually moved, announces it.
    ///
    /// <para><b>A description is a §9 broadcast, and the reason is Stage 3 rather than today.</b> The
    /// guest picker does not render descriptions yet — it is a <c>&lt;select&gt;</c>, and a sentence inside
    /// an option label is the problem the picker rewrite exists to fix — so today this publish reaches a
    /// subscriber that ignores it. It is still the right call: <c>MenuChanged</c> means "re-read the menu"
    /// and nothing else, and a workflow that decided which columns were worth announcing would be a
    /// workflow that has to be edited again the moment a surface starts reading one.</para>
    /// </summary>
    Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>Moves one item to an absolute position (§7) and, if it actually moved, announces it.</summary>
    Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

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
/// <see cref="IMenuAvailability"/> and <see cref="IMenuAdministration"/>.
///
/// <para>No metrics. §12's meter list has no menu counter, correctly — the menu changes a handful of
/// times a service, and the <c>menu_item_event</c> table is a better record of it than a counter would
/// be. So unlike <see cref="Orders.OrderWorkflow"/> and <see cref="Sittings.SittingWorkflow"/> this
/// takes no <see cref="Observability.RestaurantMetrics"/>.</para>
/// </summary>
public sealed class MenuWorkflow : IMenuWorkflow
{
    private readonly IMenuAvailability _availability;
    private readonly IMenuAdministration _administration;
    private readonly IDomainEventBroadcaster _broadcaster;

    public MenuWorkflow(
        IMenuAvailability availability,
        IMenuAdministration administration,
        IDomainEventBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(broadcaster);

        _availability = availability;
        _administration = administration;
        _broadcaster = broadcaster;
    }

    public async Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        CreateMenuItemResult result = await _administration
            .CreateMenuItemAsync(
                menuItemIdentifier, name, description, priceAmount, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // An unconditional publish, and the only one here: a create either commits or throws, so there
        // is no "nothing happened" case to guard against. A new item has to reach the open pickers or
        // nobody can order it until they reload.
        _broadcaster.Publish(new MenuChanged());

        return result;
    }

    public async Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RenameMenuItemResult result = await _administration
            .RenameMenuItemAsync(menuItemIdentifier, name, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RepriceMenuItemResult result = await _administration
            .RepriceMenuItemAsync(menuItemIdentifier, priceAmount, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DescribeMenuItemOutcome outcome = await _administration
            .DescribeMenuItemAsync(menuItemIdentifier, description, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is DescribeMenuItemOutcome.Described)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ReorderMenuItemOutcome outcome = await _administration
            .ReorderMenuItemAsync(menuItemIdentifier, displayOrder, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // §11.1 and §11.2 both render the menu in display order, so a move that committed changes what
        // every open picker shows even though no item's name, price or availability moved.
        if (outcome is ReorderMenuItemOutcome.Reordered)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
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
