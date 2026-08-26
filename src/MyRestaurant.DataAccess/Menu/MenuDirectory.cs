using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuItemSummary(
    Guid MenuItemIdentifier,
    Guid MenuSectionIdentifier,
    string MenuSectionName,
    string MenuSectionDescription,
    bool MenuSectionIsActive,
    string Name,
    string Description,
    decimal PriceAmount,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface IMenuDirectory
{
    Task<IReadOnlyList<MenuItemSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<MenuItemSummary?> GetAsync(Guid menuItemIdentifier, CancellationToken cancellationToken = default);
}

public sealed class DapperMenuDirectory : IMenuDirectory
{
    private const string MenuItemColumns = """
        menu_item.menu_item_identifier     AS MenuItemIdentifier,
        menu_item.menu_section_identifier  AS MenuSectionIdentifier,
        menu_section.name                  AS MenuSectionName,
        menu_section.description           AS MenuSectionDescription,
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
        row.MenuSectionDescription,
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
        string MenuSectionDescription,
        bool MenuSectionIsActive,
        string Name,
        string Description,
        decimal PriceAmount,
        int DisplayOrder,
        bool IsActive,
        DateTime CreatedAt);
}
