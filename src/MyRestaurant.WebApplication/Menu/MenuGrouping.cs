using MyRestaurant.DataAccess.Menu;

namespace MyRestaurant.WebApplication.Menu;

/// <summary>
/// One heading and the items filed under it, assembled for rendering (TECHNICAL_SPECIFICATION §7, §11.1,
/// §11.2).
///
/// <para>Deliberately not a <see cref="MenuSectionSummary"/>: that is the <c>menu_section</c> row —
/// position, flags, timestamps and all — read through <see cref="IMenuSectionDirectory"/>. This is a
/// <em>run</em> of <see cref="IMenuDirectory.ListAsync"/>'s one flat list, so it exists only where the
/// heading has something under it, and it carries the three heading columns that list joins rather than
/// the whole row.</para>
///
/// <para><b><see cref="MenuSectionIsActive"/> is carried rather than filtered, and which value it can
/// hold depends on which door was used.</b> <see cref="MenuGrouping.VisibleToGuests"/> drops inactive
/// headings, so every group it returns is active and the member is always <c>true</c> there — it is not
/// dead weight, because <see cref="MenuGrouping.EveryHeading"/> returns groups where it is false and
/// §11.2's "86" panel renders the difference as a chip. A record with the member absent would force the
/// kitchen to re-read a flag its own list already carries.</para>
///
/// <para><see cref="MenuSectionDescription"/> is <c>""</c> when the heading has none — the column is
/// <c>NOT NULL DEFAULT ''</c> and <c>''</c> means <em>none</em> (§7), so a surface tests
/// <see cref="string.Length"/> rather than for null. §11.1 renders it beneath the heading's name; §11.2
/// deliberately does not render it at all, because a cook reading a stock list does not need
/// "served until 11am".</para>
/// </summary>
/// <param name="MenuSectionIdentifier">The heading's UUIDv7 key, which is what a <c>@key</c> is built from — two headings can never share it where they could share a name.</param>
/// <param name="MenuSectionName">The heading's current name, joined at read time by <see cref="IMenuDirectory"/>.</param>
/// <param name="MenuSectionDescription">The heading's current description; <c>""</c> when it has none.</param>
/// <param name="MenuSectionIsActive">False when the whole heading is switched off. Always true in <see cref="MenuGrouping.VisibleToGuests"/>'s result, by construction.</param>
/// <param name="Items">The items under it, in the order <see cref="IMenuDirectory.ListAsync"/> returned them. Never empty — a group is built from items, so a heading with nothing under it produces no group at all.</param>
public sealed record MenuHeadingGroup(
    Guid MenuSectionIdentifier,
    string MenuSectionName,
    string MenuSectionDescription,
    bool MenuSectionIsActive,
    IReadOnlyList<MenuItemSummary> Items);

/// <summary>
/// Folds <see cref="IMenuDirectory.ListAsync"/>'s one flat list into the headings §11.1 and §11.2 both
/// render, and owns §7's rule about which headings each of them may see.
///
/// <para><b>Why this is a pure function outside the component (F-100).</b> The walk lived as a private
/// property inside <c>TableOrderSurface.razor</c> from M6 Slice 40, which put §11.1's grouping <em>and</em>
/// §7's two opposite-pointing visibility rules somewhere no unit test could reach — this repository has no
/// bUnit (§16.1), so the only thing asserting any of it was §16.3 scenario 17, which needs a browser, a
/// database and two and a half minutes. That is the exact situation <see cref="Orders.KitchenQueue"/>'s own
/// summary was written about: <em>a rule that can only be checked by rendering a Razor component is a rule
/// nobody checks</em>. <see cref="Orders.OrderStaging"/> and <c>OrderNarrative</c> are outside their
/// components for the same reason, and this is the fourth member of that set.</para>
///
/// <para><b>What made it a defect rather than a preference</b> is that the kitchen needed the same walk
/// with the <em>opposite</em> rule about hidden headings, and a private property cannot be called from a
/// second component. So the only way to group §11.2's panel without this file was to paste the walk into
/// it — which is F-59's mechanism exactly, one paste per surface, on surfaces nobody had decided
/// about.</para>
///
/// <para><b>Grouped by walking, not by <c>GroupBy</c>.</b> §7's read orders by
/// <c>(section.display_order, section.name, section.menu_section_identifier, item.display_order,
/// item.name, item.menu_item_identifier)</c>, so every item under one heading is <b>contiguous</b> and one
/// pass is enough. A <c>GroupBy</c> would produce the same groups in hash order and would then need
/// re-sorting by the six keys the query already applied — the ordering decision made a second time, in a
/// second file, where the two can drift. <b>Contiguity is a precondition, not an assumption this file
/// papers over:</b> a caller that hands over a list ordered some other way gets one group per run rather
/// than one group per heading, and <see cref="Orders.KitchenQueue.Build"/> makes the opposite choice for the
/// opposite reason — it re-groups from scratch because it owns the ordering rule, where this file consumes
/// one §7 owns.</para>
///
/// <para><b>Two named entry points rather than one with a flag.</b> A boolean at a call site is a rule
/// nobody reading the call site can see, and these two rules point opposite ways one sentence apart in §7,
/// which is precisely the pair the specification restates every time it mentions them because both are
/// easy to lose. The names state the rule rather than the caller, so a second guest-facing surface does
/// not need this file renamed.</para>
/// </summary>
public static class MenuGrouping
{
    /// <summary>
    /// The menu as §11.1 renders it to a guest: one group per heading a guest can see, each holding the
    /// items under it, in stored order.
    ///
    /// <para><b>An inactive heading is absent entirely, and an inactive item is present and marked</b>
    /// (§7). The two rules point opposite ways on purpose: switching off a heading is a decision about a
    /// whole part of the menu ("no breakfast this evening"), where 86ing a dish is a decision about one
    /// thing a guest is still entitled to know exists — "the guest sees that the salmon exists and is
    /// out, rather than watching it silently vanish". So the filter here is on the <em>heading's</em>
    /// flag and there is deliberately no filter on the item's; enforcing that one is the order-mutating
    /// transaction's job, under the lock (§6.5.4).</para>
    ///
    /// <para><b>An empty group cannot occur</b>, which is also §11.1's rule rather than a happy accident:
    /// groups are built from items, so a heading with nothing under it contributes nothing — and a heading
    /// with nothing under it on a guest's phone is a promise the kitchen did not make. The one surface
    /// where such a heading is visible is §11.4's index, which reads
    /// <see cref="IMenuSectionDirectory.ListAsync"/> for exactly that reason.</para>
    /// </summary>
    public static IReadOnlyList<MenuHeadingGroup> VisibleToGuests(IReadOnlyList<MenuItemSummary> menuItems)
    {
        ArgumentNullException.ThrowIfNull(menuItems);

        return Walk(menuItems, includeHeadingsHiddenFromGuests: false);
    }

    /// <summary>
    /// The menu as §11.2's "86" panel renders it to the kitchen: one group per heading that has anything
    /// under it, <b>including the headings guests cannot see</b>, each holding the items under it in
    /// stored order.
    ///
    /// <para><b>Keeping hidden headings is the rule, and §7 is the argument rather than convenience.</b>
    /// Deactivating a heading does not deactivate its items — their <c>is_active</c> is untouched, and
    /// reactivating the heading brings the menu back exactly as it was, because cascading the flag
    /// downward would silently rewrite every item's availability and lose which of them the kitchen had
    /// 86'd. This panel is the only surface in the application that can read or change those flags. Drop
    /// the hidden headings here and §7's non-cascade rule becomes unmanageable: a cook could not 86 the
    /// eggs they will need the moment breakfast is switched back on, and could not bring back something
    /// 86'd last week. The heading is marked instead, which is what the guest surface does one register
    /// down for an item.</para>
    ///
    /// <para>An empty heading is still absent, for the same structural reason as above — there is nothing
    /// on a stock list to say about a heading holding no stock, and §11.4's index is where a heading with
    /// nothing under it is visible.</para>
    /// </summary>
    public static IReadOnlyList<MenuHeadingGroup> EveryHeading(IReadOnlyList<MenuItemSummary> menuItems)
    {
        ArgumentNullException.ThrowIfNull(menuItems);

        return Walk(menuItems, includeHeadingsHiddenFromGuests: true);
    }

    /// <summary>
    /// The one pass both doors take.
    ///
    /// <para><b>The heading's three joined columns are read once per run rather than on every row, and
    /// that is the second half of F-100.</b> The original walk assigned them inside the loop body on every
    /// iteration, so a group took its name and description from the <em>last</em> row of its run while the
    /// summary above it said the first. Nothing could ever have failed on that: <c>MenuItemSummary</c>
    /// joins those columns from one <c>menu_section</c> row through an INNER JOIN, so every row of a run
    /// carries byte-identical values and the two readings cannot disagree. A claim no test can falsify is
    /// either deleted or made true (F-77), and here making it true costs one <c>if</c> — so the assignment
    /// happens where a run begins, which is also the only place a reader would look for it.</para>
    ///
    /// <para>The flush is guarded on <c>current.Count > 0</c> rather than on a sentinel identifier,
    /// because the first item admitted may arrive after any number of filtered ones and
    /// <see cref="Guid.Empty"/> is a value a heading could in principle hold.</para>
    /// </summary>
    private static IReadOnlyList<MenuHeadingGroup> Walk(
        IReadOnlyList<MenuItemSummary> menuItems,
        bool includeHeadingsHiddenFromGuests)
    {
        List<MenuHeadingGroup> headings = [];
        List<MenuItemSummary> current = [];

        Guid currentIdentifier = Guid.Empty;
        string currentName = string.Empty;
        string currentDescription = string.Empty;
        bool currentIsActive = false;

        foreach (MenuItemSummary item in menuItems)
        {
            if (!item.MenuSectionIsActive && !includeHeadingsHiddenFromGuests)
            {
                continue;
            }

            if (current.Count > 0 && item.MenuSectionIdentifier != currentIdentifier)
            {
                headings.Add(new MenuHeadingGroup(
                    currentIdentifier, currentName, currentDescription, currentIsActive, current));
                current = [];
            }

            if (current.Count == 0)
            {
                currentIdentifier = item.MenuSectionIdentifier;
                currentName = item.MenuSectionName;
                currentDescription = item.MenuSectionDescription;
                currentIsActive = item.MenuSectionIsActive;
            }

            current.Add(item);
        }

        if (current.Count > 0)
        {
            headings.Add(new MenuHeadingGroup(
                currentIdentifier, currentName, currentDescription, currentIsActive, current));
        }

        return headings;
    }
}
