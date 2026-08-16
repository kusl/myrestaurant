using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// One menu item as every reading surface sees it (TECHNICAL_SPECIFICATION §7, §11.1, §11.2).
///
/// <para><see cref="IsActive"/> is carried rather than filtered on, because §7 is explicit that a
/// deactivated item is <b>not</b> hidden from the guest: it stays on the menu marked "currently
/// unavailable" and cannot be added to a send — "the guest sees that the salmon exists and is out,
/// rather than watching it silently vanish". A caller that filters this list is almost certainly
/// wrong; the one place the flag is enforced is the order-mutating transaction, which re-reads it
/// under the lock (§6.5.4).</para>
///
/// <para><b><see cref="MenuSectionIsActive"/> is the one exception, and §7 states it as such.</b> An
/// inactive <em>section</em> is not rendered to the guest at all — the opposite rule to an inactive item,
/// one paragraph away from it. The two are not a contradiction: switching off a heading is a decision
/// about a whole part of the menu ("no breakfast this evening"), where 86ing an item is a decision about
/// one dish that guests are still entitled to see exists. Both flags are carried here and neither is
/// filtered, because §11.4's administrator has to see everything and it is the guest surface that owes
/// §7 the distinction.</para>
///
/// <para><see cref="Description"/> is never <c>null</c>. The column is <c>NOT NULL DEFAULT ''</c> and
/// <c>''</c> means "none", so a surface tests <see cref="string.Length"/> rather than for null — the
/// reason is the paired CHECK on <c>menu_item_event</c>, which could not tie an optional payload to its
/// event type if clearing a description wrote NULL. The same rule and the same reason as
/// <see cref="MenuSectionSummary.Description"/>.</para>
///
/// <para><see cref="DisplayOrder"/> is where somebody put the item <em>within its section</em>, not where
/// the alphabet puts it. Before <c>0005</c> it was a menu-wide number that nothing ever set; since
/// <c>0005</c> an item is created at the end of its own heading and the number means something.</para>
/// </summary>
/// <param name="MenuItemIdentifier">The item's UUIDv7 primary key (ADR-0011).</param>
/// <param name="MenuSectionIdentifier">The heading this item is filed under. NOT NULL since <c>0005</c> — §7: an item under no heading is an item nobody decided about.</param>
/// <param name="MenuSectionName">That heading's current name, joined at read time — so a renamed section reads under its new name everywhere at once.</param>
/// <param name="MenuSectionIsActive">False when the whole heading is switched off. §7: such a section is <b>not</b> rendered to the guest, unlike an inactive item.</param>
/// <param name="Name">The item's current name (§7 — renames are logged in <c>menu_item_event</c>).</param>
/// <param name="Description">The item's current description; <c>""</c> when it has none.</param>
/// <param name="PriceAmount">The item's current price. Lines already added keep the price captured at add time (§6.5.4).</param>
/// <param name="DisplayOrder">Where the item sits within its section; ties are broken by name, then identifier.</param>
/// <param name="IsActive">False when the item is "86'd" — visible, unorderable (§7, §11.2).</param>
/// <param name="CreatedAt">When the item was first created.</param>
public sealed record MenuItemSummary(
    Guid MenuItemIdentifier,
    Guid MenuSectionIdentifier,
    string MenuSectionName,
    bool MenuSectionIsActive,
    string Name,
    string Description,
    decimal PriceAmount,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// Reads the menu (TECHNICAL_SPECIFICATION §7). This is the read side only: creating, renaming,
/// repricing, describing, reordering, and activating/deactivating items — each of which also appends a
/// <c>menu_item_event</c> row — is menu administration, behind its own write interface, exactly the way
/// <see cref="Tables.ITableDirectory"/> stands beside <see cref="Tables.ITableAdministration"/>.
///
/// <para>The guest staging area picks items from this list (§11.1), the kitchen's "86" panel lists them
/// (§11.2), and the order-mutating transaction prices every added line from the stored
/// <c>price_amount</c> rather than from anything the client sent (§6.5.4).</para>
/// </summary>
public interface IMenuDirectory
{
    /// <summary>
    /// Every menu item, active and inactive, under every section, active and inactive, in the order the
    /// guest staging area and the kitchen "86" panel both render (§7, §11.1, §11.2).
    ///
    /// <para><b>Ordered by section first as of <c>0005</c>: <c>(section.display_order, section.name,
    /// section.menu_section_identifier, item.display_order, item.name, item.menu_item_identifier)</c>.</b>
    /// That is six keys and every one of them earns its place — the first three put the headings in the
    /// order somebody chose, and the last three do the same for the items under each. The two identifier
    /// tiebreaks are there because the schema permits equal positions deliberately (§8.2), and without
    /// them two items at position 0 would render in whatever sequence the scan happened to return.</para>
    ///
    /// <para><b>One list rather than a list of sections each holding a list, and that is a ruling.</b>
    /// The ordering above means every item under one heading is <em>contiguous</em>, so a surface groups
    /// by walking the list once and starting a new heading when
    /// <see cref="MenuItemSummary.MenuSectionIdentifier"/> changes. A nested shape would need either a
    /// second query per section or a join materialised into objects here, and it would put a section with
    /// no items into the result — which no reading surface wants: §11.1 renders headings that have
    /// something under them, and an empty heading on a guest's phone is a promise the kitchen did not
    /// make. An administrator who needs to see the empty ones reads
    /// <see cref="IMenuSectionDirectory.ListAsync"/>, which is the list of headings themselves.</para>
    /// </summary>
    Task<IReadOnlyList<MenuItemSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One item by identifier, or <c>null</c> when no item has that identifier.</summary>
    Task<MenuItemSummary?> GetAsync(Guid menuItemIdentifier, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuDirectory"/>. One connection per call from the singleton
/// <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns aliased to the
/// record's member names, and rows read into an internal row type with a <see cref="DateTime"/> member
/// before being projected — Npgsql materialises <c>timestamptz</c> as <see cref="DateTime"/> and Dapper's
/// constructor binding will not feed one into a <see cref="DateTimeOffset"/> parameter (the same fix
/// <c>TableDirectory</c>, <c>PersonDirectory</c>, and <c>SittingDirectory</c> carry).
///
/// <para>The join to <c>menu_section</c> is INNER, and that is <c>0005</c>'s <c>NOT NULL</c> being
/// collected on: the reference cannot be absent, so a LEFT JOIN would only invite two nullable members
/// for a row the schema forbids. It is also why every column below is table-qualified — <c>menu_item</c>
/// and <c>menu_section</c> both carry a <c>name</c>, a <c>description</c>, a <c>display_order</c>, an
/// <c>is_active</c> and a <c>created_at</c>, and an unqualified reference to any of the five is
/// PostgreSQL error 42702 waiting to happen.</para>
/// </summary>
public sealed class DapperMenuDirectory : IMenuDirectory
{
    private const string MenuItemColumns = """
        menu_item.menu_item_identifier     AS MenuItemIdentifier,
        menu_item.menu_section_identifier  AS MenuSectionIdentifier,
        menu_section.name                  AS MenuSectionName,
        menu_section.is_active             AS MenuSectionIsActive,
        menu_item.name                     AS Name,
        menu_item.description              AS Description,
        menu_item.price_amount             AS PriceAmount,
        menu_item.display_order            AS DisplayOrder,
        menu_item.is_active                AS IsActive,
        menu_item.created_at               AS CreatedAt
        """;

    private const string MenuItemFrom = """
        FROM menu_item
        INNER JOIN menu_section
                ON menu_section.menu_section_identifier = menu_item.menu_section_identifier
        """;

    private static readonly string ListSql = $"""
        SELECT {MenuItemColumns}
        {MenuItemFrom}
        ORDER BY menu_section.display_order,
                 menu_section.name,
                 menu_section.menu_section_identifier,
                 menu_item.display_order,
                 menu_item.name,
                 menu_item.menu_item_identifier;
        """;

    private static readonly string GetSql = $"""
        SELECT {MenuItemColumns}
        {MenuItemFrom}
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuItemSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemRow> rows = await connection.QueryAsync<MenuItemRow>(new CommandDefinition(
            ListSql,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<MenuItemSummary?> GetAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemRow? row = await connection.QuerySingleOrDefaultAsync<MenuItemRow>(new CommandDefinition(
            GetSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    private static MenuItemSummary ToSummary(MenuItemRow row) => new(
        row.MenuItemIdentifier,
        row.MenuSectionIdentifier,
        row.MenuSectionName,
        row.MenuSectionIsActive,
        row.Name,
        row.Description,
        row.PriceAmount,
        row.DisplayOrder,
        row.IsActive,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed record MenuItemRow(
        Guid MenuItemIdentifier,
        Guid MenuSectionIdentifier,
        string MenuSectionName,
        bool MenuSectionIsActive,
        string Name,
        string Description,
        decimal PriceAmount,
        int DisplayOrder,
        bool IsActive,
        DateTime CreatedAt);
}
