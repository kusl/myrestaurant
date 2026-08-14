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
/// <para><see cref="Description"/> is never <c>null</c>. The column is <c>NOT NULL DEFAULT ''</c> and
/// <c>''</c> means "none", so a surface tests <see cref="string.Length"/> rather than for null — the
/// reason is the paired CHECK on <c>menu_item_event</c>, which could not tie an optional payload to its
/// event type if clearing a description wrote NULL. The same rule and the same reason as
/// <see cref="MenuSectionSummary.Description"/>.</para>
///
/// <para><see cref="DisplayOrder"/> is where somebody put the item, not where the alphabet puts it.
/// Every item created before <c>0005</c> sits at 0, so the ordering reads as the name ordering this
/// table has always had — see <see cref="IMenuDirectory.ListAsync"/> for why that is deliberate rather
/// than a placeholder.</para>
/// </summary>
/// <param name="MenuItemIdentifier">The item's UUIDv7 primary key (ADR-0011).</param>
/// <param name="Name">The item's current name (§7 — renames are logged in <c>menu_item_event</c>).</param>
/// <param name="Description">The item's current description; <c>""</c> when it has none.</param>
/// <param name="PriceAmount">The item's current price. Lines already added keep the price captured at add time (§6.5.4).</param>
/// <param name="DisplayOrder">Where the item sits; ties are broken by name, then identifier.</param>
/// <param name="IsActive">False when the item is "86'd" — visible, unorderable (§7, §11.2).</param>
/// <param name="CreatedAt">When the item was first created.</param>
public sealed record MenuItemSummary(
    Guid MenuItemIdentifier,
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
    /// Every menu item, active and inactive, in display order — the order the guest staging area and the
    /// kitchen "86" panel both render (§7, §11.1, §11.2).
    ///
    /// <para><b>Ordered by <c>(display_order, name, menu_item_identifier)</c> as of <c>0004</c>, and the
    /// change is deliberately invisible.</b> <c>display_order</c> defaults to 0 and nothing assigns
    /// anything else until <c>0005</c> gives an item a section to be positioned within, so on every tree
    /// that exists today this is the <c>(name, identifier)</c> ordering the method has always had. It is
    /// written in the final shape now rather than later because the alternative is a second edit to the
    /// same two queries in the slice that can least afford one — and because it is the shape §7 asks
    /// for: alphabetical is wrong on a real menu, since "Fries" before "Truffle Fries" is an ordering
    /// somebody chose and <c>ORDER BY name</c> cannot express it.</para>
    ///
    /// <para>The two tiebreakers are there so that equal positions, which the schema permits
    /// deliberately, still render the same way on every request rather than in whatever sequence the
    /// scan returned. Today every position is equal, so the tiebreak is doing all of the work.</para>
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
/// </summary>
public sealed class DapperMenuDirectory : IMenuDirectory
{
    private const string MenuItemColumns = """
        menu_item.menu_item_identifier AS MenuItemIdentifier,
        menu_item.name                 AS Name,
        menu_item.description          AS Description,
        menu_item.price_amount         AS PriceAmount,
        menu_item.display_order        AS DisplayOrder,
        menu_item.is_active            AS IsActive,
        menu_item.created_at           AS CreatedAt
        """;

    private static readonly string ListSql = $"""
        SELECT {MenuItemColumns}
        FROM menu_item
        ORDER BY menu_item.display_order, menu_item.name, menu_item.menu_item_identifier;
        """;

    private static readonly string GetSql = $"""
        SELECT {MenuItemColumns}
        FROM menu_item
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
        row.Name,
        row.Description,
        row.PriceAmount,
        row.DisplayOrder,
        row.IsActive,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed record MenuItemRow(
        Guid MenuItemIdentifier,
        string Name,
        string Description,
        decimal PriceAmount,
        int DisplayOrder,
        bool IsActive,
        DateTime CreatedAt);
}
