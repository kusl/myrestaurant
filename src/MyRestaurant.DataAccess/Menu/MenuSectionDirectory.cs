using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuSectionSummary(
    Guid MenuSectionIdentifier,
    string Name,
    string Description,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface IMenuSectionDirectory
{
    Task<IReadOnlyList<MenuSectionSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<MenuSectionSummary?> GetAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuSectionDirectory : IMenuSectionDirectory
{
    private const string MenuSectionColumns = """
        menu_section.menu_section_identifier AS MenuSectionIdentifier,
        menu_section.name                    AS Name,
        menu_section.description             AS Description,
        menu_section.display_order           AS DisplayOrder,
        menu_section.is_active               AS IsActive,
        menu_section.created_at              AS CreatedAt
        """;

    private static readonly string ListSql = $"""
        SELECT {MenuSectionColumns}
        FROM menu_section
        ORDER BY menu_section.display_order, menu_section.name, menu_section.menu_section_identifier;
        """;

    private static readonly string GetSql = $"""
        SELECT {MenuSectionColumns}
        FROM menu_section
        WHERE menu_section.menu_section_identifier = @MenuSectionIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuSectionDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuSectionSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuSectionRow> rows = await connection.QueryAsync<MenuSectionRow>(
            new CommandDefinition(
                ListSql,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<MenuSectionSummary?> GetAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionRow? row = await connection.QuerySingleOrDefaultAsync<MenuSectionRow>(
            new CommandDefinition(
                GetSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    private static MenuSectionSummary ToSummary(MenuSectionRow row) => new(
        row.MenuSectionIdentifier,
        row.Name,
        row.Description,
        row.DisplayOrder,
        row.IsActive,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed record MenuSectionRow(
        Guid MenuSectionIdentifier,
        string Name,
        string Description,
        int DisplayOrder,
        bool IsActive,
        DateTime CreatedAt);
}
