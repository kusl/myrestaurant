using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>The outcome of <see cref="IMenuAdministration.CreateMenuItemAsync"/> (§7).</summary>
public enum CreateMenuItemOutcome
{
    /// <summary>The item row was written, under the section the caller named.</summary>
    Created,

    /// <summary>
    /// No section has that identifier, so nothing was written. §7 requires every item to be under a
    /// heading, and this is that requirement reported rather than raised: without it the caller would
    /// meet PostgreSQL error 23503 from the foreign key, which names a constraint instead of naming the
    /// thing a person did wrong.
    /// </summary>
    MenuSectionNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.RenameMenuItemAsync"/> (§7).</summary>
public enum RenameMenuItemOutcome
{
    /// <summary>The name changed and a <c>name_changed</c> event was written.</summary>
    Renamed,

    /// <summary>The new name equalled the current one; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.RepriceMenuItemAsync"/> (§7).</summary>
public enum RepriceMenuItemOutcome
{
    /// <summary>The price changed and a <c>price_changed</c> event was written.</summary>
    Repriced,

    /// <summary>The new price equalled the current one; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.DescribeMenuItemAsync"/> (§7).</summary>
public enum DescribeMenuItemOutcome
{
    /// <summary>The description changed and a <c>description_changed</c> event was written.</summary>
    Described,

    /// <summary>The new description equalled the current one; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.MoveMenuItemToSectionAsync"/> (§7).</summary>
///
/// <remarks>
/// Four members where <see cref="ReorderMenuItemOutcome"/> has three, and the fourth is the whole
/// difference between the two verbs: a position is a number and always exists, where a heading is a row
/// that may not. <see cref="MenuSectionNotFound"/> is the same outcome
/// <see cref="CreateMenuItemOutcome.MenuSectionNotFound"/> reports and it exists for the same reason —
/// without it the caller meets PostgreSQL error 23503 from the foreign key, which names a constraint
/// instead of naming the thing a person did wrong.
/// </remarks>
public enum MoveMenuItemToSectionOutcome
{
    /// <summary>
    /// The item is under a different heading now, appended to the end of it, and the events say so.
    /// </summary>
    Moved,

    /// <summary>The item was already filed under that heading; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,

    /// <summary>No section has that identifier; nothing was written.</summary>
    MenuSectionNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.ReorderMenuItemAsync"/> (§7).</summary>
public enum ReorderMenuItemOutcome
{
    /// <summary>The position changed and a <c>reordered</c> event was written.</summary>
    Reordered,

    /// <summary>The item was already at that position; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.ResequenceMenuItemsAsync"/> (§7).</summary>
public enum ResequenceMenuItemsOutcome
{
    /// <summary>
    /// At least one item moved. Every item whose position changed was written, and each of those wrote its
    /// own <c>reordered</c> event; the ones already in place wrote nothing.
    /// </summary>
    Resequenced,

    /// <summary>
    /// The requested order is the stored order, item for item; nothing was written. This is the ordinary
    /// answer to pressing "move up" on a dish somebody else moved up a second earlier.
    /// </summary>
    NoChange,

    /// <summary>
    /// The list is not a permutation of the items filed under that heading — one is missing from it, one
    /// appears twice, or one is named that is filed somewhere else. Nothing was written.
    ///
    /// <para>Refused rather than reconciled, on the ruling
    /// <see cref="ResequenceMenuSectionsOutcome.MenuSectionSetChanged"/> records one register up: a list
    /// that disagrees with the table came from a page rendered before somebody else changed the menu, and
    /// a partially obeyed stale ordering is an order nobody chose.</para>
    ///
    /// <para><b>It is also the answer to a heading this menu does not hold, and there is deliberately no
    /// fourth case for that.</b> An unknown heading has no items under it, so it reaches this outcome
    /// through the same comparison every other refusal does — and from the surface's side the two are one
    /// fact, <em>this page is stale, reload it</em>. A distinction the caller cannot act on differently is
    /// a distinction not worth returning, which is the same reasoning that gave the section verb three
    /// outcomes for three shapes of refusal.</para>
    /// </summary>
    MenuItemSetChanged,
}

/// <summary>
/// A created menu item, as stored (§7). Every member is what the row and its events actually carry, which
/// is not necessarily what the caller passed: the name and the description are trimmed, the price is
/// rounded to the column's own <c>numeric(10,2)</c> scale, and <see cref="DisplayOrder"/> is
/// <em>assigned</em> rather than supplied — so a surface can echo this back without a second read and
/// without lying by two hundredths or by a position.
/// </summary>
/// <param name="Outcome">Whether the item was written, or the section named nothing.</param>
/// <param name="MenuItemIdentifier">The identifier the caller minted (ADR-0011), now a <c>menu_item</c> primary key.</param>
/// <param name="MenuSectionIdentifier">The heading the item was filed under.</param>
/// <param name="MenuSectionName">That heading's name, so a confirmation can say it without a second read; <c>null</c> when the section was not found.</param>
/// <param name="Name">The stored name; <c>null</c> when the section was not found.</param>
/// <param name="Description">The stored description, <c>""</c> for none; <c>null</c> when the section was not found.</param>
/// <param name="PriceAmount">The stored price; <c>null</c> when the section was not found.</param>
/// <param name="DisplayOrder">The position the item was appended at within its section; <c>null</c> when the section was not found.</param>
public sealed record CreateMenuItemResult(
    CreateMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    Guid MenuSectionIdentifier,
    string? MenuSectionName,
    string? Name,
    string? Description,
    decimal? PriceAmount,
    int? DisplayOrder)
{
    /// <summary>True only when a row was written — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Created => Outcome is CreateMenuItemOutcome.Created;

    /// <summary>
    /// True when the create also wrote a <c>description_changed</c> event, which happens exactly when a
    /// non-blank description was supplied — <c>created</c> deliberately carries the name and the price
    /// only (§8.2), so a description set at creation time is a second event in the same transaction.
    /// </summary>
    public bool DescriptionWasSet => Description is { Length: > 0 };
}

/// <summary>
/// The outcome of one rename, carrying both names so a confirmation can say what it used to be — which
/// is the whole reason §7 logs renames rather than silently overwriting a column.
/// </summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="MenuItemIdentifier">The item the attempt named.</param>
/// <param name="Name">The stored name after the call; <c>null</c> when the item does not exist.</param>
/// <param name="PreviousName">The name before the call; <c>null</c> when the item does not exist.</param>
public sealed record RenameMenuItemResult(
    RenameMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    string? PreviousName)
{
    /// <summary>True only when the name actually moved — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Changed => Outcome is RenameMenuItemOutcome.Renamed;

    /// <summary>True unless the identifier named nothing.</summary>
    public bool ItemExists => Outcome is not RenameMenuItemOutcome.MenuItemNotFound;
}

/// <summary>
/// The outcome of one reprice, carrying both prices for the same reason
/// <see cref="RenameMenuItemResult"/> carries both names.
/// </summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="MenuItemIdentifier">The item the attempt named.</param>
/// <param name="Name">The item's name, for a confirmation that can then avoid a second read; <c>null</c> when it does not exist.</param>
/// <param name="PriceAmount">The stored price after the call; <c>null</c> when the item does not exist.</param>
/// <param name="PreviousPriceAmount">The price before the call; <c>null</c> when the item does not exist.</param>
public sealed record RepriceMenuItemResult(
    RepriceMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    decimal? PriceAmount,
    decimal? PreviousPriceAmount)
{
    /// <summary>True only when the price actually moved — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Changed => Outcome is RepriceMenuItemOutcome.Repriced;

    /// <summary>True unless the identifier named nothing.</summary>
    public bool ItemExists => Outcome is not RepriceMenuItemOutcome.MenuItemNotFound;
}

/// <summary>
/// Menu administration (TECHNICAL_SPECIFICATION §7, §11.4: "Menu (CRUD + activity, event history per
/// item)") — creating an item, renaming it, repricing it, describing it, repositioning it and refiling it
/// under another heading, each writing the <c>menu_item</c> row and its mirroring
/// <c>menu_item_event</c> in one transaction.
///
/// <para><b>Why availability is not here.</b> <see cref="IMenuAvailability"/> already owns the
/// activate/deactivate write, and it stays there: §7 gives that one verb to kitchen and counter as well
/// as to administrators, because the kitchen is the surface that knows the salmon has run out, and
/// §11.2 puts the toggle on the kitchen board. Everything on <em>this</em> interface is administrator
/// only (§11.4). Two interfaces, two audiences, one event log — which is the point of the log.</para>
///
/// <para><b>Why every verb is its own call rather than one edit.</b> §7's event vocabulary has
/// <c>name_changed</c>, <c>price_changed</c>, <c>description_changed</c> and <c>reordered</c> as distinct
/// types with mutually exclusive payload columns, enforced by §8.2's four named paired CHECKs. A combined
/// "save" that moved several would have to write several events anyway, and would then have to decide what
/// to do when one of them is a no-op. Separate calls make the log read the way somebody investigating a
/// price dispute needs it to.</para>
///
/// <para><b>The no-op rule, and it now governs five verbs.</b> A rename to the name it already has, a
/// reprice to the price it already has, a description equal to the stored one, a move to the position it
/// is already at, and a refile into the heading it is already under all write nothing. §11.4's per-item
/// history is meant to be read by a person, and an append-only log of "somebody pressed Save" is
/// noise.</para>
///
/// <para><b>Prices on existing order lines never move.</b> §6.5.4 captures <c>unit_price_amount</c> into
/// the adding operation, so repricing changes what the <em>next</em> line costs and nothing that is
/// already on a bill. <c>OrderReadModelTests</c> owns that fact against a real database; nothing here
/// needs to defend it.</para>
///
/// <para><b>Names are not unique, deliberately.</b> <c>menu_item.name</c> carries no UNIQUE constraint
/// (§8.2), unlike <c>restaurant_table.label</c>, so nothing here rejects a duplicate: a real kitchen
/// runs "Soup" as a rotating special, and inventing a constraint the schema of record does not have
/// would be this layer overruling it. The index page orders by name, so duplicates sit next to each
/// other where somebody will notice them.</para>
/// </summary>
public interface IMenuAdministration
{
    /// <summary>
    /// Creates a menu item, active, under the section the caller names, and writes the matching
    /// <c>created</c> event (which carries both the name and the price — §8.2's CHECK requires both for
    /// that type). The identifier is minted by the caller (ADR-0011) so a surface can link straight to
    /// the new item.
    ///
    /// <para><b>An item is now appended at <c>MAX(display_order) + 1</c> within its section, and until
    /// <c>0005</c> it was created at 0.</b> The reason for the old rule was that "the end of the menu"
    /// was not a defined place while an item had no heading, so a menu-wide number would have handed out
    /// positions this migration then had to undo. An item does have a heading now, so the end of a
    /// section is exactly where a newly created dish belongs — which is also the rule
    /// <see cref="IMenuSectionAdministration.CreateMenuSectionAsync"/> has followed since <c>0003</c>,
    /// and the two are finally the same rule. <c>MAX + 1</c> rather than <c>COUNT(*)</c>: a count
    /// collides with an existing position the moment anything is ever moved.</para>
    ///
    /// <para><b>Three events, not one, when everything is supplied.</b> §8.2 keeps <c>created</c> at the
    /// name and the price, so an item created under a heading with a description writes <c>created</c>,
    /// then <c>section_changed</c>, then <c>description_changed</c> — one transaction, three rows. The
    /// position writes no event at all, because <c>menu_item_event</c>'s display-order CHECK binds that
    /// payload to <c>reordered</c> alone and every <c>created</c> row already in a database was written
    /// without one.</para>
    ///
    /// <para>The name and description are trimmed; the price is rounded to two decimals before any row is
    /// written, so the row and its events can never disagree about what was set. Returns what was
    /// stored.</para>
    /// </summary>
    /// <param name="menuItemIdentifier">A caller-minted UUIDv7 (ADR-0011).</param>
    /// <param name="menuSectionIdentifier">The heading to file it under. §7 requires one; an unknown identifier is reported rather than raised.</param>
    /// <param name="name">What the receipt, the kitchen ticket and the bill all read.</param>
    /// <param name="description">Optional prose under the name; <c>null</c> or blank stores <c>""</c> and writes no third event.</param>
    /// <param name="priceAmount">The price the next order line will capture (§6.5.4).</param>
    /// <param name="actorPersonIdentifier">The administrator the events record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priceAmount"/> is negative or does not fit <c>numeric(10,2)</c>.</exception>
    Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames one item and appends a <c>name_changed</c> event carrying the new name. A rename to the
    /// name it already has writes nothing: an append-only log of "somebody pressed Rename" is noise, and
    /// §11.4's per-item history is meant to be read by a person.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reprices one item and appends a <c>price_changed</c> event carrying the new price, on the same
    /// no-op terms as <see cref="RenameMenuItemAsync"/>. The comparison is made after rounding, so
    /// asking for 4.500 when the stored price is 4.50 is correctly a no-op rather than an event that
    /// records nothing having happened.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priceAmount"/> is negative or does not fit <c>numeric(10,2)</c>.</exception>
    Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears one item's description and appends a <c>description_changed</c> event carrying the
    /// new value, on the same no-op terms as <see cref="RenameMenuItemAsync"/>. Clearing writes <c>""</c>
    /// and is an ordinary change with an ordinary event, not a deletion: the column is <c>NOT NULL</c>
    /// precisely so that §8.2's paired CHECK can stay an equality.
    ///
    /// <para>The comparison is ordinal, because the column is <c>text</c>. Recasing a description is a
    /// change every guest can read, and this layer is not the place to decide it did not happen.</para>
    /// </summary>
    Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one item to an absolute position and appends a <c>reordered</c> event carrying it.
    /// Positions are not unique: two items may share one, and the reads break the tie by name. That is
    /// deliberate (§8.2) — a unique ordering column would make every move a two-phase rewrite.
    ///
    /// <para><b>Until <c>0005</c> the position is a menu-wide one; afterwards it is a position within a
    /// section.</b> Nothing about this verb changes then, which is the reason it is written now: the
    /// column, the event type and the CHECK are the same either way, and the only thing <c>0005</c> adds
    /// is a second dimension for the reads to group on.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayOrder"/> is negative.</exception>
    Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reorders <b>every item filed under one heading</b> at once, assigning positions <c>0…n-1</c> from
    /// the order given and appending one <c>reordered</c> event per item whose position actually changed.
    ///
    /// <para><b>It exists because <see cref="ReorderMenuItemAsync"/> cannot serve a Move-up button</b>, and
    /// the reason is the one <see cref="IMenuSectionAdministration.ResequenceMenuSectionsAsync"/> records
    /// one register up: that verb writes an <em>absolute</em> position, positions within a heading are
    /// deliberately non-unique with a name tie-break (§8.2), so "move this dish up" is not expressible as
    /// one absolute write — two items sharing a number have an order nobody assigned and no single number
    /// distinguishes them. A pairwise swap is not expressible either, because it would have to decide what
    /// happens when the two numbers are equal. Taking the <em>whole</em> ordering leaves nothing to decide:
    /// the caller sends the list it is already rendering with two entries exchanged, and the stored
    /// positions become that list's indices.</para>
    ///
    /// <para><b>The set is one heading's items rather than the whole menu, and that is the only place this
    /// differs from the section verb.</b> A position is a position <em>within</em> a section (§7), so the
    /// heading is a parameter rather than something inferred from the list — which is also what makes an
    /// empty list against an empty heading a well-formed no-op instead of a special case. Items under other
    /// headings are neither read nor locked nor moved: reordering the drinks cannot touch the puddings.</para>
    ///
    /// <para><b>A list that is not a permutation of that heading's items is refused whole rather than
    /// reconciled.</b> Short, repeating an identifier, or naming an item filed elsewhere all mean the page
    /// was rendered before the menu changed, and the answer is
    /// <see cref="ResequenceMenuItemsOutcome.MenuItemSetChanged"/> with nothing written. An unknown heading
    /// arrives at the same answer through the same comparison, because it has no items under it.</para>
    ///
    /// <para><b>One event per item that moved, not one per item</b>, on the no-op rule this interface
    /// applies everywhere else: resequencing eight dishes to move one of them writes the two rows whose
    /// positions changed, so §11.4's per-item history stays a record of decisions rather than of button
    /// presses.</para>
    ///
    /// <para><b>All the events of one call share an instant</b>, because one transaction stamps every row it
    /// writes with one <see cref="IClock.UtcNow"/>. They read in the order the rows were written because
    /// <see cref="IIdentifierFactory"/> hands out ascending values inside a millisecond (§8.1) — the
    /// property <b>F-95</b> found nothing was keeping, and the same property the section verb leans on.</para>
    /// </summary>
    /// <param name="menuSectionIdentifier">The heading whose items are being reordered; one this menu does not hold is reported rather than raised.</param>
    /// <param name="orderedMenuItemIdentifiers">Every item under that heading, exactly once, in the order they should read.</param>
    /// <param name="actorPersonIdentifier">The administrator each written event records.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="orderedMenuItemIdentifiers"/> is null.</exception>
    Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Files one item under a different heading and appends a <c>section_changed</c> event carrying the
    /// new one — the last verb of the menu enhancement to acquire a caller, and the one
    /// <see cref="CreateMenuItemAsync"/> has been writing half of since <c>0005</c>.
    ///
    /// <para><b>The item is appended to the end of its new section, and that is a ruling rather than a
    /// convenience.</b> A position is <em>within</em> a section (§7), so carrying the old number across
    /// would drop the dish into the middle of the target heading at whatever place that number happens to
    /// name — a position somebody chose for a different list. Appending at <c>MAX(display_order) + 1</c>
    /// under a lock on the target section row is exactly what <see cref="CreateMenuItemAsync"/> does, and
    /// after this verb the two are the same rule: an item arriving in a heading, however it arrives,
    /// arrives at the end of it.</para>
    ///
    /// <para><b>Two events, or one.</b> §8.2 binds <c>new_display_order</c> to <c>reordered</c> alone, so
    /// a move that also changes the position must say so in a second event rather than move a number the
    /// log does not mention. It is conditional on the position actually differing, on the no-op rule this
    /// interface applies everywhere else — a move into an empty heading from position 0 lands at 0 again,
    /// and an event reading <em>moved to position 0</em> beside an unchanged column is the somebody-pressed-Save
    /// noise §11.4's history exists to avoid. The order is <c>section_changed</c> then
    /// <c>reordered</c>, which is the order a person reads them in.</para>
    ///
    /// <para><b>Nothing else about the item moves.</b> Its name, price, description and <c>is_active</c>
    /// are untouched: an 86'd dish moved between headings is still 86'd, on the same reasoning §7 gives
    /// for a deactivated section not cascading to its items.</para>
    ///
    /// <para>A move to the heading the item is already under writes nothing and reports
    /// <see cref="MoveMenuItemToSectionOutcome.NoChange"/>. That case is decided before the target
    /// section is read, which is sound rather than an ordering accident: an item cannot already be under
    /// a heading that does not exist.</para>
    /// </summary>
    /// <param name="menuItemIdentifier">The item to refile.</param>
    /// <param name="menuSectionIdentifier">The heading to file it under; an unknown identifier is reported rather than raised.</param>
    /// <param name="actorPersonIdentifier">The administrator the events record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuAdministration"/>. One connection and one transaction
/// per operation, one <see cref="IClock.UtcNow"/> instant on both rows of each write, a UUIDv7 event key
/// from <see cref="IIdentifierFactory"/> (ADR-0011) — the shape every write in this layer has, and the
/// shape <see cref="DapperMenuAvailability"/> already established for this table.
///
/// <para>The row is taken <c>FOR UPDATE</c> before it is compared, for the reason
/// <see cref="DapperMenuAvailability"/> takes it: without the lock, two administrators repricing the
/// same item at once could both read 4.50, both write 5.00, and log two <c>price_changed</c> events for
/// one change. The price would still be right and the history would be a lie, which is the worse of the
/// two failures in an append-only system (ADR-0002).</para>
/// </summary>
public sealed class DapperMenuAdministration : IMenuAdministration
{
    /// <summary>Stored spellings of <c>menu_item_event.event_type</c> (§8.2's CHECK).</summary>
    private const string CreatedEventType = "created";

    private const string NameChangedEventType = "name_changed";

    private const string PriceChangedEventType = "price_changed";

    /// <summary>
    /// <c>description_changed</c>, not <c>described</c>. <c>menu_section_event</c> spells the same verb
    /// the second way, and the asymmetry is deliberate: each table's vocabulary is internally consistent,
    /// and this one has said <c>name_changed</c> and <c>price_changed</c> since <c>0001</c>.
    /// </summary>
    private const string DescriptionChangedEventType = "description_changed";

    /// <summary>
    /// <c>section_changed</c>, added by <c>0005</c>. It is written on every create as well as on every
    /// move, because §7 requires an item to be under a heading and §8.2 keeps <c>created</c> at the name
    /// and the price — so this type is the only place the log can record which heading.
    ///
    /// <para><b>It had exactly one writer for three slices, and that was the state
    /// <see cref="MoveMenuItemToSectionAsync"/> ends.</b> Until this verb existed, every row of this type
    /// in every database came from a create, so an item's heading was decided once and never again —
    /// which is why a heading created with a typo could only be worked around by making another. Anything
    /// counting an item's events should note that a create still contributes one of these before a move
    /// contributes a second.</para>
    /// </summary>
    private const string SectionChangedEventType = "section_changed";

    private const string ReorderedEventType = "reordered";

    /// <summary>
    /// The column is <c>numeric(10,2)</c>: eight digits before the point, two after. A larger value is
    /// PostgreSQL error 22003 at INSERT time, which would surface as an opaque exception well after the
    /// form that caused it; refusing it here names the problem.
    /// </summary>
    private const decimal PriceExclusiveUpperBound = 100_000_000m;

    /// <summary>
    /// <c>display_order</c> is supplied as of <c>0005</c>, where <c>0004</c> omitted it and let the
    /// column's <c>DEFAULT 0</c> apply. The old rule existed because "the end of the menu" was undefined
    /// while an item had no heading; an item has one now, so it is appended to the end of its own
    /// section, which is what <see cref="DapperMenuSectionAdministration"/> has always done for sections.
    /// </summary>
    private const string InsertMenuItemSql = """
        INSERT INTO menu_item (
            menu_item_identifier, menu_section_identifier, name, description,
            price_amount, display_order, is_active, created_at)
        VALUES (
            @MenuItemIdentifier, @MenuSectionIdentifier, @Name, @Description,
            @PriceAmount, @DisplayOrder, true, @CreatedAt);
        """;

    /// <summary>
    /// Takes the <em>section</em> row before reading the highest position under it, and the lock target
    /// is the interesting half. Locking the section is what serialises two administrators creating an
    /// item under the same heading at the same moment: without it both read the same <c>MAX</c>, both
    /// write the same position, and the menu has two dishes claiming one place — which the schema permits
    /// (positions are deliberately not unique, §8.2) and which is therefore a defect nothing would ever
    /// report. It is the same reasoning every other write in this file locks the item row for, one table
    /// up, and it doubles as the existence check the foreign key would otherwise make on the caller's
    /// behalf in the language of constraint names.
    ///
    /// <para><c>COALESCE(MAX(...) + 1, 0)</c>, so the first item under a new heading is at 0 and the
    /// aggregate over no rows does not return NULL. <c>MAX + 1</c> rather than <c>COUNT(*)</c> for the
    /// reason <see cref="DapperMenuSectionAdministration"/> records: a count collides with a position
    /// that already exists as soon as anything has ever been moved.</para>
    /// </summary>
    private const string LockMenuSectionAndReadNextPositionSql = """
        SELECT menu_section.name AS Name,
               COALESCE(
                   (SELECT MAX(menu_item.display_order) + 1
                    FROM menu_item
                    WHERE menu_item.menu_section_identifier = menu_section.menu_section_identifier),
                   0) AS NextDisplayOrder
        FROM menu_section
        WHERE menu_section.menu_section_identifier = @MenuSectionIdentifier
        FOR UPDATE;
        """;

    /// <summary>
    /// <c>menu_section_identifier</c> is selected as of <see cref="MoveMenuItemToSectionAsync"/>, and it
    /// is the one column here no other verb in this file reads. It is on the shared lock read rather than
    /// on a second query of its own because the comparison a move makes — <em>is it already under that
    /// heading</em> — has to be made against the row this transaction is holding, and a second read would
    /// be a second chance for the answer to be stale.
    /// </summary>
    private const string LockMenuItemSql = """
        SELECT menu_item.name                   AS Name,
               menu_item.description            AS Description,
               menu_item.price_amount           AS PriceAmount,
               menu_item.display_order          AS DisplayOrder,
               menu_item.menu_section_identifier AS MenuSectionIdentifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    private const string UpdateNameSql = """
        UPDATE menu_item
        SET name = @Name
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string UpdatePriceSql = """
        UPDATE menu_item
        SET price_amount = @PriceAmount
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string UpdateDescriptionSql = """
        UPDATE menu_item
        SET description = @Description
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string UpdateDisplayOrderSql = """
        UPDATE menu_item
        SET display_order = @DisplayOrder
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    /// <summary>
    /// Every item under one heading with its position, locked for the duration of a resequence and
    /// <b>ordered by identifier</b> — the order is what stops two concurrent resequences deadlocking half
    /// way through each other's set, which is the rule
    /// <see cref="DapperMenuSectionAdministration"/> established one register up.
    ///
    /// <para><b>The section row is deliberately NOT locked, and the argument is arithmetic rather than
    /// preference.</b> A concurrent create or refile appends into this heading at
    /// <c>MAX(display_order) + 1</c>, computed from the very positions this statement is holding: if those
    /// are <c>n</c> rows whose maximum is <c>m</c>, then <c>m ≥ n - 1</c>, so the arrival lands at
    /// <c>m + 1 ≥ n</c> — strictly after every position a resequence of those <c>n</c> rows can assign,
    /// which is <em>exactly</em> the append those two verbs promise. The interleaving is therefore correct
    /// with no lock at all, and taking none means this verb adds no new lock ordering to reason about: it
    /// takes item locks and nothing else, where <see cref="IMenuAdministration.MoveMenuItemToSectionAsync"/>
    /// takes an item lock and then a section lock. A section lock here would invert that nesting and make
    /// the deadlock question live for the first time.</para>
    ///
    /// <para>An unknown heading returns no rows, which is what makes it indistinguishable here from an
    /// empty one — see <see cref="ResequenceMenuItemsOutcome.MenuItemSetChanged"/> for why that is the
    /// answer rather than a gap.</para>
    /// </summary>
    private const string LockMenuItemsInSectionSql = """
        SELECT menu_item.menu_item_identifier AS MenuItemIdentifier,
               menu_item.display_order        AS DisplayOrder
        FROM menu_item
        WHERE menu_item.menu_section_identifier = @MenuSectionIdentifier
        ORDER BY menu_item.menu_item_identifier
        FOR UPDATE;
        """;

    /// <summary>
    /// The one UPDATE in this file that moves two columns, and they move together because a position is
    /// meaningless without the heading it is a position within (§7). Splitting this into two statements
    /// would leave a moment inside the transaction where the item is under its new heading at a position
    /// belonging to its old one — invisible to every other session, and exactly the sort of intermediate
    /// state that becomes visible the first time somebody adds a statement between the two.
    /// </summary>
    private const string UpdateMenuSectionAndPositionSql = """
        UPDATE menu_item
        SET menu_section_identifier = @MenuSectionIdentifier,
            display_order = @DisplayOrder
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    /// <summary>
    /// One INSERT for every event type this file writes. §8.2's named paired CHECKs tie each
    /// nullable payload column to exactly the types that carry it — <c>created</c> the name and the price,
    /// <c>name_changed</c> the name alone, <c>price_changed</c> the price alone,
    /// <c>description_changed</c> the description alone, <c>reordered</c> the position alone — so the
    /// callers below pass NULL for whichever the type must not have, and the database refuses any
    /// combination this file gets wrong.
    ///
    /// <para><c>created</c> carries the name and the price and <b>neither</b> the description nor the
    /// section, although the <c>menu_item</c> row is inserted with both. That is <c>0004</c>'s ruling
    /// extended by <c>0005</c> rather than an oversight: a description is optional and a section arrived
    /// later, so widening <c>created</c> to carry either would relax an equality to an implication, and
    /// every <c>created</c> row already in a database was written without them.
    /// <see cref="CreateMenuItemAsync"/> therefore writes up to three events in its one transaction.</para>
    ///
    /// <para>The position is on the row and in no event. §8.2 binds <c>new_display_order</c> to
    /// <c>reordered</c> alone, and that equality is the reason: a <c>created</c> event carrying a
    /// position would be false of every row written before <c>0005</c>.</para>
    /// </summary>
    private const string InsertMenuItemEventSql = """
        INSERT INTO menu_item_event (
            menu_item_event_identifier, menu_item_identifier, actor_person_identifier,
            event_type, new_name, new_price_amount, new_description, new_display_order,
            new_menu_section_identifier, occurred_at)
        VALUES (
            @MenuItemEventIdentifier, @MenuItemIdentifier, @ActorPersonIdentifier,
            @EventType, @NewName, @NewPriceAmount, @NewDescription, @NewDisplayOrder,
            @NewMenuSectionIdentifier, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuAdministration(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
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
        string normalizedName = NormalizeName(name);
        string normalizedDescription = NormalizeDescription(description);
        decimal normalizedPrice = NormalizePrice(priceAmount);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The section is locked and the next position read in one round trip, before anything is
        // written. A missing row here is the caller naming a heading that does not exist — reported
        // rather than left to the foreign key, which would answer in the language of constraint names.
        MenuSectionPositionRow? section = await connection
            .QuerySingleOrDefaultAsync<MenuSectionPositionRow>(new CommandDefinition(
                LockMenuSectionAndReadNextPositionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CreateMenuItemResult(
                CreateMenuItemOutcome.MenuSectionNotFound,
                menuItemIdentifier,
                menuSectionIdentifier,
                MenuSectionName: null,
                Name: null,
                Description: null,
                PriceAmount: null,
                DisplayOrder: null);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuItemSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
                MenuSectionIdentifier = menuSectionIdentifier,
                Name = normalizedName,
                Description = normalizedDescription,
                PriceAmount = normalizedPrice,
                DisplayOrder = section.NextDisplayOrder,
                CreatedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            CreatedEventType,
            normalizedName,
            normalizedPrice,
            newDescription: null,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        // A second event in the same transaction, and unconditional: §7 requires every item to be under a
        // heading, so "filed under Starters" is always a thing that happened. §8.2 keeps 'created' at the
        // name and the price, so this is where the log records it.
        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            SectionChangedEventType,
            newName: null,
            newPriceAmount: null,
            newDescription: null,
            newDisplayOrder: null,
            menuSectionIdentifier,
            now,
            cancellationToken).ConfigureAwait(false);

        // A third, and only when there is something to record — a description is optional where a section
        // is not. A blank one is not an event at all, on the same no-op rule every other verb here
        // honours: an append-only log of "somebody left a field empty" is noise.
        if (normalizedDescription.Length > 0)
        {
            await InsertEventAsync(
                connection,
                transaction,
                menuItemIdentifier,
                actorPersonIdentifier,
                DescriptionChangedEventType,
                newName: null,
                newPriceAmount: null,
                normalizedDescription,
                newDisplayOrder: null,
                newMenuSectionIdentifier: null,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CreateMenuItemResult(
            CreateMenuItemOutcome.Created,
            menuItemIdentifier,
            menuSectionIdentifier,
            section.Name,
            normalizedName,
            normalizedDescription,
            normalizedPrice,
            section.NextDisplayOrder);
    }

    public async Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RenameMenuItemResult(
                RenameMenuItemOutcome.MenuItemNotFound, menuItemIdentifier, Name: null, PreviousName: null);
        }

        // name is `text`, not citext, so compare ordinally — "Soup" and "soup" are two different names,
        // and renaming between them is a real change somebody meant to make.
        if (string.Equals(item.Name, normalizedName, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RenameMenuItemResult(
                RenameMenuItemOutcome.NoChange, menuItemIdentifier, item.Name, item.Name);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateNameSql,
            new { MenuItemIdentifier = menuItemIdentifier, Name = normalizedName },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            NameChangedEventType,
            normalizedName,
            newPriceAmount: null,
            newDescription: null,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RenameMenuItemResult(
            RenameMenuItemOutcome.Renamed, menuItemIdentifier, normalizedName, item.Name);
    }

    public async Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        decimal normalizedPrice = NormalizePrice(priceAmount);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RepriceMenuItemResult(
                RepriceMenuItemOutcome.MenuItemNotFound,
                menuItemIdentifier,
                Name: null,
                PriceAmount: null,
                PreviousPriceAmount: null);
        }

        // decimal == compares value, not scale, so 4.50 read back from numeric(10,2) equals a 4.5 the
        // caller typed. The comparison is after rounding, so 4.499 is a change and 4.500 is not.
        if (item.PriceAmount == normalizedPrice)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RepriceMenuItemResult(
                RepriceMenuItemOutcome.NoChange,
                menuItemIdentifier,
                item.Name,
                item.PriceAmount,
                item.PriceAmount);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdatePriceSql,
            new { MenuItemIdentifier = menuItemIdentifier, PriceAmount = normalizedPrice },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            PriceChangedEventType,
            newName: null,
            normalizedPrice,
            newDescription: null,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RepriceMenuItemResult(
            RepriceMenuItemOutcome.Repriced,
            menuItemIdentifier,
            item.Name,
            normalizedPrice,
            item.PriceAmount);
    }

    public async Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedDescription = NormalizeDescription(description);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DescribeMenuItemOutcome.MenuItemNotFound;
        }

        if (string.Equals(item.Description, normalizedDescription, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DescribeMenuItemOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateDescriptionSql,
            new { MenuItemIdentifier = menuItemIdentifier, Description = normalizedDescription },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            DescriptionChangedEventType,
            newName: null,
            newPriceAmount: null,
            normalizedDescription,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return DescribeMenuItemOutcome.Described;
    }

    public async Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(displayOrder);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReorderMenuItemOutcome.MenuItemNotFound;
        }

        if (item.DisplayOrder == displayOrder)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReorderMenuItemOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateDisplayOrderSql,
            new { MenuItemIdentifier = menuItemIdentifier, DisplayOrder = displayOrder },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            ReorderedEventType,
            newName: null,
            newPriceAmount: null,
            newDescription: null,
            displayOrder,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ReorderMenuItemOutcome.Reordered;
    }

    public async Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedMenuItemIdentifiers);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemPositionRow> locked = await connection
            .QueryAsync<MenuItemPositionRow>(new CommandDefinition(
                LockMenuItemsInSectionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The identifier is the table's primary key, so this cannot collide.
        Dictionary<Guid, int> storedPositions = locked
            .ToDictionary(row => row.MenuItemIdentifier, row => row.DisplayOrder);

        if (!IsPermutationOf(orderedMenuItemIdentifiers, storedPositions))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ResequenceMenuItemsOutcome.MenuItemSetChanged;
        }

        int moved = 0;

        for (int position = 0; position < orderedMenuItemIdentifiers.Count; position++)
        {
            Guid menuItemIdentifier = orderedMenuItemIdentifiers[position];

            if (storedPositions[menuItemIdentifier] == position)
            {
                // The no-op rule, per row: a resequence that leaves six of eight dishes where they were
                // writes two events rather than eight (§7).
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                UpdateDisplayOrderSql,
                new { MenuItemIdentifier = menuItemIdentifier, DisplayOrder = position },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await InsertEventAsync(
                connection,
                transaction,
                menuItemIdentifier,
                actorPersonIdentifier,
                ReorderedEventType,
                newName: null,
                newPriceAmount: null,
                newDescription: null,
                position,
                newMenuSectionIdentifier: null,
                now,
                cancellationToken).ConfigureAwait(false);

            moved++;
        }

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ResequenceMenuItemsOutcome.NoChange;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ResequenceMenuItemsOutcome.Resequenced;
    }

    /// <summary>
    /// The only verb here that takes two locks, and the order they are taken in is the ruling.
    ///
    /// <para>The <em>item</em> row is locked first and the <em>section</em> row second, which is the same
    /// direction every other write in this file already runs in — the item verbs lock an item and nothing
    /// else, and <see cref="CreateMenuItemAsync"/> locks a section and nothing else, so item-then-section
    /// is the only nesting that exists and it is consistent. Two administrators moving two different
    /// dishes into each other's headings therefore cannot deadlock: both take their item lock first, and
    /// neither is holding a section lock while waiting for one.</para>
    /// </summary>
    public async Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MoveMenuItemToSectionOutcome.MenuItemNotFound;
        }

        // Decided before the target is read, and that is sound rather than lucky: an item cannot already
        // be filed under a heading that does not exist, so this arm can never hide a MenuSectionNotFound.
        if (item.MenuSectionIdentifier == menuSectionIdentifier)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MoveMenuItemToSectionOutcome.NoChange;
        }

        MenuSectionPositionRow? section = await connection
            .QuerySingleOrDefaultAsync<MenuSectionPositionRow>(new CommandDefinition(
                LockMenuSectionAndReadNextPositionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MoveMenuItemToSectionOutcome.MenuSectionNotFound;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateMenuSectionAndPositionSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
                MenuSectionIdentifier = menuSectionIdentifier,
                DisplayOrder = section.NextDisplayOrder,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            SectionChangedEventType,
            newName: null,
            newPriceAmount: null,
            newDescription: null,
            newDisplayOrder: null,
            menuSectionIdentifier,
            now,
            cancellationToken).ConfigureAwait(false);

        // A second event only when the number actually moved. §8.2 binds new_display_order to 'reordered'
        // alone, so the position cannot ride along on the event above — and a heading that happens to
        // append at the position the item already held is a move that changed one thing, not two.
        if (section.NextDisplayOrder != item.DisplayOrder)
        {
            await InsertEventAsync(
                connection,
                transaction,
                menuItemIdentifier,
                actorPersonIdentifier,
                ReorderedEventType,
                newName: null,
                newPriceAmount: null,
                newDescription: null,
                section.NextDisplayOrder,
                newMenuSectionIdentifier: null,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return MoveMenuItemToSectionOutcome.Moved;
    }

    private async Task InsertEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newName,
        decimal? newPriceAmount,
        string? newDescription,
        int? newDisplayOrder,
        Guid? newMenuSectionIdentifier,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuItemEventSql,
            new
            {
                MenuItemEventIdentifier = _identifierFactory.Create(),
                MenuItemIdentifier = menuItemIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                NewName = newName,
                NewPriceAmount = newPriceAmount,
                NewDescription = newDescription,
                NewDisplayOrder = newDisplayOrder,
                NewMenuSectionIdentifier = newMenuSectionIdentifier,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task<MenuItemLockRow?> ReadLockedAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<MenuItemLockRow>(new CommandDefinition(
            LockMenuItemSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    /// <summary>
    /// Rounds to the column's own scale, away from zero — which is what PostgreSQL's <c>numeric</c> does,
    /// so rounding here rather than letting the database do it silently means the value returned to the
    /// caller and the value in both rows are the same number.
    /// </summary>
    private static decimal NormalizePrice(decimal priceAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priceAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(priceAmount, PriceExclusiveUpperBound);

        return Math.Round(priceAmount, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Trims, and turns null or blank into <c>""</c> — the stored spelling of "no description" (§8.2).
    /// There is no length ceiling here because the column has none: inventing one would be this layer
    /// overruling the schema of record, which is the same reason nothing here rejects a duplicate item
    /// name. The identical rule and the identical reason as
    /// <see cref="DapperMenuSectionAdministration"/>'s.
    /// </summary>
    private static string NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();

    /// <summary>
    /// Whether the requested ordering is exactly the set of items this heading holds, which is the one
    /// precondition <see cref="ResequenceMenuItemsAsync"/> has.
    ///
    /// <para><b>It de-duplicates before it resolves, and that ordering is load-bearing.</b> A list of the
    /// right length whose every member exists can still name one of them twice — the one shape a length
    /// check and a membership check each admit on their own — so the distinct count is compared to the
    /// requested count first. The same three-line shape and the same reason as
    /// <see cref="DapperMenuSectionAdministration"/>'s, deliberately not shared between them: the two read
    /// different tables inside different transactions, and a helper spanning both would be a class whose
    /// only content is a set comparison the BCL already spells.</para>
    /// </summary>
    private static bool IsPermutationOf(
        IReadOnlyList<Guid> requested,
        Dictionary<Guid, int> storedPositions)
    {
        if (requested.Count != storedPositions.Count)
        {
            return false;
        }

        HashSet<Guid> distinct = [.. requested];

        return distinct.Count == requested.Count && distinct.All(storedPositions.ContainsKey);
    }

    // Dapper maps these positional records by constructor-parameter name against the aliased columns above.
    private sealed record MenuSectionPositionRow(string Name, int NextDisplayOrder);

    // The two columns a resequence needs from every row under a heading: which item, and where it sits.
    private sealed record MenuItemPositionRow(
        Guid MenuItemIdentifier,
        int DisplayOrder);

    private sealed record MenuItemLockRow(
        string Name,
        string Description,
        decimal PriceAmount,
        int DisplayOrder,
        Guid MenuSectionIdentifier);
}
