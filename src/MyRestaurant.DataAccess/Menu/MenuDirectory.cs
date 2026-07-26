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
/// </summary>
/// <param name="MenuItemIdentifier">The item's UUIDv7 primary key (ADR-0011).</param>
/// <param name="Name">The item's current name (§7 — renames are logged in <c>menu_item_event</c>).</param>
/// <param name="PriceAmount">The item's current price. Lines already added keep the price captured at add time (§6.5.4).</param>
/// <param name="IsActive">False when the item is "86'd" — visible, unorderable (§7, §11.2).</param>
/// <param name="CreatedAt">When the item was first created.</param>
public sealed record MenuItemSummary(
    Guid MenuItemIdentifier,
    string Name,
    decimal PriceAmount,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// Reads the menu (TECHNICAL_SPECIFICATION §7). This is the read side only: creating, renaming,
/// repricing, and activating/deactivating items — each of which also appends a <c>menu_item_event</c>
/// row — is menu administration and lands with M5 (§19), behind its own write interface, exactly the
/// way <see cref="Tables.ITableDirectory"/> stands beside <see cref="Tables.ITableAdministration"/>.
///
/// <para>It exists now because ordering cannot be built without it: the guest staging area picks items
/// from this list (§11.1), the kitchen's "86" panel lists them (§11.2), and the order-mutating
/// transaction prices every added line from the stored <c>price_amount</c> rather than from anything the
/// client sent (§6.5.4).</para>
/// </summary>
public interface IMenuDirectory
{
    /// <summary>
    /// Every menu item, active and inactive, ordered by name then identifier — the order the guest
    /// staging area and the kitchen "86" panel both render (§7, §11.1, §11.2).
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
        menu_item.price_amount         AS PriceAmount,
        menu_item.is_active            AS IsActive,
        menu_item.created_at           AS CreatedAt
        """;

    private static readonly string ListSql = $"""
        SELECT {MenuItemColumns}
        FROM menu_item
        ORDER BY menu_item.name, menu_item.menu_item_identifier;
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
        row.PriceAmount,
        row.IsActive,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed record MenuItemRow(
        Guid MenuItemIdentifier,
        string Name,
        decimal PriceAmount,
        bool IsActive,
        DateTime CreatedAt);
}
