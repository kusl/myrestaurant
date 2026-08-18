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
/// <para>One workflow over three write services, because there is one notification. §9 fires
/// <see cref="MenuChanged"/> on "a menu item or <c>menu_item_event</c> commit" without distinguishing
/// which verb caused it, and every subscriber responds the same way: re-read the menu. Splitting this in
/// two would make it possible to wire an application that announces 86s and not repricings.</para>
///
/// <para><b>All six of <see cref="IMenuSectionAdministration"/>'s verbs are here now, and the obligation
/// carried since Slice 37 is closed.</b> The rule never changed: a workflow verb with no caller is a code
/// path no test can reach through the interface meant to protect it, so a verb arrives when its surface
/// does. <c>0005</c> gave exactly one of them a caller — the section create page — and the section editor
/// gives the other four theirs in one slice, because rename, describe, reorder and set-active are four
/// forms on one page and shipping any subset would leave the same hole under a smaller name.</para>
///
/// <para><b>And with <see cref="MoveMenuItemToSectionAsync"/> the rule has no outstanding case at all.</b>
/// That verb was the last one in the whole menu enhancement written without a surface — deferred by name
/// in three consecutive slices rather than quietly omitted, which is what made it possible to say when it
/// arrived. Every method on this interface is now reachable from a form an administrator can open, so a
/// reader looking for the untested part of this file will not find one here.</para>
///
/// <para><b>The rename is the one that had stopped being latent.</b> §11.1's guest menu groups items under
/// their headings, so a rename that announced nothing would leave a stale heading in every open picker
/// until that page happened to reload — and set-active is worse, because §7 hides an inactive section from
/// the guest entirely, so a heading switched off without a broadcast leaves a whole part of the menu
/// orderable on every phone already looking at it. Both are now ordinary conditional publishes.</para>
///
/// <para><b>Not every call publishes.</b> A rename to the name it already has, a reprice to the price it
/// already has, a description equal to the stored one, a move to the position it is already at, a
/// resequence into the order already stored, and a toggle to the state it is already in all commit
/// nothing, and announcing them would tell every open surface in the building to re-query for a change
/// that did not happen. The write services report that distinction; this file's whole job is to honour
/// it.</para>
/// </summary>
public interface IMenuWorkflow
{
    /// <summary>
    /// Creates a menu section (§7, §11.4) and announces it when a row was written.
    ///
    /// <para><b>Conditional, where <see cref="CreateMenuItemAsync"/> used to be unconditional and no
    /// longer is.</b> A section create can fail on the <c>citext</c> UNIQUE — a second "Drinks" spelled
    /// any way at all — and a name collision commits nothing, so announcing it would tell every phone in
    /// the building to re-query for a heading that does not exist.</para>
    ///
    /// <para><b>Why announce at all, when a brand-new section holds no items and §11.1 renders no empty
    /// headings?</b> Because <c>MenuChanged</c> means "re-read the menu" and nothing else. A workflow that
    /// decided which writes were worth announcing would be a workflow that has to be edited again the
    /// moment a surface starts rendering one — which is the argument this file already makes about a
    /// description, and it was right then.</para>
    /// </summary>
    Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames one section (§7) and, if the name actually moved, announces it.
    ///
    /// <para><b>The publish matters more here than for an item rename, and the reason is §11.1.</b> The
    /// guest menu groups items under their headings, so the heading's name is rendered on every open
    /// picker in the building — and unlike an item's name it is rendered even when nothing under it
    /// changed. A rename that committed and announced nothing would leave the old word on every phone
    /// until that page happened to reload.</para>
    ///
    /// <para>Conditional on <c>Renamed</c> alone: <c>NoChange</c>, <c>NameTaken</c> and
    /// <c>MenuSectionNotFound</c> each commit nothing, and the second is an ordinary mis-tap — the column
    /// is <c>citext</c>, so a second "Drinks" spelled any way at all is refused.</para>
    /// </summary>
    Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears one section's description (§7) and, if it actually moved, announces it.
    ///
    /// <para><b>This publish reaches no guest surface today, and it is still the right call</b> — the
    /// same argument <see cref="DescribeMenuItemAsync"/> makes and for the same reason. §11.1 renders a
    /// heading's name and not its description, because the guest menu groups from
    /// <c>MenuItemSummary</c>, which carries the one and not the other. <c>MenuChanged</c> means "re-read
    /// the menu" and nothing else; a workflow that decided which columns were worth announcing would be a
    /// workflow that has to be edited again the moment a surface starts reading one.</para>
    /// </summary>
    Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one section to an absolute position (§7) and, if it actually moved, announces it. §11.1
    /// renders the headings in <c>(display_order, name, identifier)</c>, so a move that committed changes
    /// the order of the whole guest menu even though no item moved at all.
    /// </summary>
    Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reorders the whole set of headings at once (§7) and, if anything actually moved, announces it.
    ///
    /// <para><b>This is what an up/down control on the menu index posts</b>, and it is here rather than
    /// left to <see cref="ReorderMenuSectionAsync"/> because "up" is not an absolute position: §7 permits
    /// two headings to share one, so the number that would express the move depends on what else is
    /// sharing. The surface sends the ordering it is already rendering with two entries exchanged.</para>
    ///
    /// <para>Conditional on <c>Resequenced</c> alone. <c>NoChange</c> is an administrator pressing "up" on
    /// a heading somebody else has already moved up, and <c>MenuSectionSetChanged</c> is a page rendered
    /// before a heading was created — both commit nothing, so both announce nothing, and the surface
    /// reloads rather than reporting a success that did not happen.</para>
    ///
    /// <para>The publish is one <c>MenuChanged</c> for the whole call, whatever number of rows it wrote,
    /// which is what §9's "re-read the menu" already means. §11.1 renders the headings in stored order, so
    /// a resequence changes the shape of every open guest menu without a single item having moved.</para>
    /// </summary>
    Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
        IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches a whole heading on or off (§7) and, if the flag actually moved, announces it.
    ///
    /// <para><b>This is the loudest of the five and the one whose broadcast is not optional.</b> §7 hides
    /// an inactive section from the guest <em>entirely</em> — the opposite of the rule one paragraph away
    /// for an inactive item, which stays visible and marked. So switching a heading off removes a whole
    /// part of the menu from every open picker, and a flip that announced nothing would leave those items
    /// tappable on every phone already looking at them until the send was refused server-side for a reason
    /// the guest never saw coming (§6.5.9).</para>
    ///
    /// <para>Deactivating a section does <b>not</b> deactivate its items (§7): their <c>is_active</c> is
    /// untouched, so reactivating the heading brings the menu back exactly as it was, 86s and all. That is
    /// the write service's business rather than this file's, and it is restated here because a workflow is
    /// where somebody would reach to add the cascade.</para>
    /// </summary>
    Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a menu item under a section (§11.4) and announces it when a row was written.
    ///
    /// <para><b>This publish became conditional in <c>0005</c>, and that is a behaviour change worth
    /// naming.</b> A create used either to commit or to throw, so there was no "nothing happened" case;
    /// since §7 requires every item to be under a heading, naming one that does not exist is now an
    /// ordinary reported outcome rather than an exception — and an outcome that wrote nothing must
    /// announce nothing, on the rule this whole file exists to honour.</para>
    ///
    /// <para>The section and the description travel with it and are stored on the row; the write service
    /// appends <c>section_changed</c> always and <c>description_changed</c> when there is one, in the same
    /// transaction, because §8.2's <c>created</c> carries the name and the price only. That is the write
    /// service's business, not this file's — one commit, one announcement, whether it wrote two events or
    /// three.</para>
    /// </summary>
    Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
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
    /// Reorders every item under one heading at once (§7) and, if anything actually moved, announces it.
    ///
    /// <para><b>This is what an up/down control on a group's item rows posts</b>, and it is here rather than
    /// left to <see cref="ReorderMenuItemAsync"/> for the reason
    /// <see cref="ResequenceMenuSectionsAsync"/> is here rather than left to
    /// <see cref="ReorderMenuSectionAsync"/>: "up" is not an absolute position, because §7 permits two items
    /// under one heading to share one. The surface sends the ordering it is already rendering for that
    /// heading with two entries exchanged.</para>
    ///
    /// <para>Conditional on <c>Resequenced</c> alone. <c>NoChange</c> is an administrator pressing Up on a
    /// dish somebody else has already moved up, and <c>MenuItemSetChanged</c> is a page rendered before an
    /// item was created, refiled or the heading itself vanished from the request — all of them commit
    /// nothing, so all of them announce nothing, and the surface reloads rather than reporting a success
    /// that did not happen.</para>
    ///
    /// <para>The publish is one <c>MenuChanged</c> for the whole call whatever number of rows it wrote,
    /// which is what §9's "re-read the menu" already means. §11.1 renders each heading's items in stored
    /// order, so a resequence changes what every open guest picker shows without a single price, name or
    /// availability flag having moved.</para>
    /// </summary>
    Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Files one item under a different heading (§7) and, if it actually moved, announces it.
    ///
    /// <para><b>This is the last verb of the menu enhancement to arrive, and the obligation it discharges
    /// is the one every paragraph above is about.</b> A workflow verb with no caller is a code path no
    /// test can reach through the interface meant to protect it, so a verb arrives when its surface does —
    /// five section verbs arrived that way and this is the sixth and last. Nothing behind
    /// <see cref="IMenuWorkflow"/> is now unreachable from a form.</para>
    ///
    /// <para><b>The publish is as loud as a section visibility flip and for the same reason.</b> §11.1
    /// groups the guest menu by heading, so a refile moves a card out of one grouping and into another on
    /// every open picker in the building — and if the destination is an inactive heading the card leaves
    /// the guest's menu <em>entirely</em>, because §7 does not render such a heading at all. A move that
    /// committed and announced nothing would leave a dish tappable under a heading it is no longer in,
    /// until the send was refused server-side for a reason the guest never saw coming (§6.5.9).</para>
    ///
    /// <para>Conditional on <c>Moved</c> alone. <c>NoChange</c>, <c>MenuItemNotFound</c> and
    /// <c>MenuSectionNotFound</c> each commit nothing, and the third is an ordinary stale form rather than
    /// a fault: a heading can be renamed or a page left open.</para>
    /// </summary>
    Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
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
/// <see cref="IMenuAvailability"/>, <see cref="IMenuAdministration"/> and — for its one verb with a
/// caller — <see cref="IMenuSectionAdministration"/>.
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
    private readonly IMenuSectionAdministration _sections;
    private readonly IDomainEventBroadcaster _broadcaster;

    public MenuWorkflow(
        IMenuAvailability availability,
        IMenuAdministration administration,
        IMenuSectionAdministration sections,
        IDomainEventBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(broadcaster);

        _availability = availability;
        _administration = administration;
        _sections = sections;
        _broadcaster = broadcaster;
    }

    public async Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        CreateMenuSectionResult result = await _sections
            .CreateMenuSectionAsync(
                menuSectionIdentifier, name, description, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Created)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RenameMenuSectionOutcome outcome = await _sections
            .RenameMenuSectionAsync(menuSectionIdentifier, name, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // §11.1 renders the heading above every card under it, so a committed rename changes what every
        // open picker in the building shows even though no item moved.
        if (outcome is RenameMenuSectionOutcome.Renamed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DescribeMenuSectionOutcome outcome = await _sections
            .DescribeMenuSectionAsync(
                menuSectionIdentifier, description, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is DescribeMenuSectionOutcome.Described)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ReorderMenuSectionOutcome outcome = await _sections
            .ReorderMenuSectionAsync(
                menuSectionIdentifier, displayOrder, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ReorderMenuSectionOutcome.Reordered)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
        IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ResequenceMenuSectionsOutcome outcome = await _sections
            .ResequenceMenuSectionsAsync(
                orderedMenuSectionIdentifiers, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ResequenceMenuSectionsOutcome.Resequenced)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        MenuSectionActivationOutcome outcome = await _sections
            .SetMenuSectionActiveAsync(
                menuSectionIdentifier, isActive, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // §7: an inactive section is not rendered to the guest at all, so this flip adds or removes a
        // whole part of every open menu. A no-op flip — two administrators pressing the same button
        // seconds apart — committed nothing and must announce nothing, on this file's standing rule.
        if (outcome is MenuSectionActivationOutcome.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        CreateMenuItemResult result = await _administration
            .CreateMenuItemAsync(
                menuItemIdentifier,
                menuSectionIdentifier,
                name,
                description,
                priceAmount,
                actorPersonIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        // Guarded as of 0005, where this was the one unconditional publish in the file. A create used to
        // commit or throw; §7's NOT NULL heading makes "that section does not exist" an ordinary outcome
        // that writes nothing, and announcing it would send every open picker back to the database for a
        // change that did not happen.
        if (result.Created)
        {
            _broadcaster.Publish(new MenuChanged());
        }

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

    public async Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ResequenceMenuItemsOutcome outcome = await _administration
            .ResequenceMenuItemsAsync(
                menuSectionIdentifier,
                orderedMenuItemIdentifiers,
                actorPersonIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        // One announcement for the whole call, however many rows it wrote: MenuChanged means "re-read the
        // menu" and nothing else (§9), so publishing per row would tell every open phone to re-query
        // several times for one decision.
        if (outcome is ResequenceMenuItemsOutcome.Resequenced)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        MoveMenuItemToSectionOutcome outcome = await _administration
            .MoveMenuItemToSectionAsync(
                menuItemIdentifier, menuSectionIdentifier, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // §11.1 groups the guest menu by heading, so a committed refile moves a card between groupings on
        // every open picker — and into an inactive heading it removes the card from the guest's menu
        // outright, because §7 renders no such heading at all.
        if (outcome is MoveMenuItemToSectionOutcome.Moved)
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
