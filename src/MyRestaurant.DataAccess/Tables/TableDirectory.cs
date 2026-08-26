using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Tables;

public sealed record RestaurantTableSummary(
    Guid TableIdentifier,
    string Label,
    bool IsActive,
    DateTimeOffset? JoinSecretRotatedAt,
    DateTimeOffset CreatedAt);

public interface ITableDirectory
{
    Task<IReadOnlyList<RestaurantTableSummary>> ListTablesAsync(CancellationToken cancellationToken = default);

    Task<RestaurantTableSummary?> GetTableAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);
}

public sealed class DapperTableDirectory : ITableDirectory
{
    private const string TableColumns = """
        restaurant_table_identifier AS TableIdentifier,
        label                       AS Label,
        is_active                   AS IsActive,
        join_secret_rotated_at      AS JoinSecretRotatedAt,
        created_at                  AS CreatedAt
        """;

    private static readonly string ListSql = $"""
        SELECT {TableColumns}
        FROM restaurant_table
        ORDER BY created_at, label;
        """;

    private static readonly string ByIdSql = $"""
        SELECT {TableColumns}
        FROM restaurant_table
        WHERE restaurant_table_identifier = @TableIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperTableDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<RestaurantTableSummary>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<RestaurantTableRow> rows = await connection.QueryAsync<RestaurantTableRow>(
            new CommandDefinition(ListSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<RestaurantTableSummary?> GetTableAsync(Guid tableIdentifier, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        RestaurantTableRow? row = await connection.QuerySingleOrDefaultAsync<RestaurantTableRow>(new CommandDefinition(
            ByIdSql,
            new { TableIdentifier = tableIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    private static RestaurantTableSummary ToSummary(RestaurantTableRow row) => new(
        row.TableIdentifier,
        row.Label,
        row.IsActive,
        row.JoinSecretRotatedAt is { } rotatedAt
            ? new DateTimeOffset(DateTime.SpecifyKind(rotatedAt, DateTimeKind.Utc))
            : null,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed record RestaurantTableRow(
        Guid TableIdentifier,
        string Label,
        bool IsActive,
        DateTime? JoinSecretRotatedAt,
        DateTime CreatedAt);
}
