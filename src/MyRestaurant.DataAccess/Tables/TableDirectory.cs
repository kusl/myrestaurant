using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Tables;

/// <summary>
/// A read-only projection of a <c>restaurant_table</c> row for the administration tables area
/// (TECHNICAL_SPECIFICATION §4.1, §11.4). It deliberately omits the <c>join_secret</c> — that value is
/// server-only and is never sent to any client (§4.1) — and carries <see cref="JoinSecretRotatedAt"/>
/// (null when the secret has never been rotated) so the admin can see when tokens were last cut.
/// </summary>
/// <param name="TableIdentifier">The table's UUIDv7 primary key (ADR-0011).</param>
/// <param name="Label">The unique, human label (e.g. "Table 5", §4.1).</param>
/// <param name="IsActive">False once deactivated — token validation and display rendering stop (§4.1).</param>
/// <param name="JoinSecretRotatedAt">When the join secret was last rotated, or <c>null</c> if never (§4.1).</param>
/// <param name="CreatedAt">Row creation timestamp (UTC).</param>
public sealed record RestaurantTableSummary(
    Guid TableIdentifier,
    string Label,
    bool IsActive,
    DateTimeOffset? JoinSecretRotatedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Reads tables for the administration area (TECHNICAL_SPECIFICATION §4.1, §11.4). This is the
/// read-only reporting companion to <see cref="ITableAdministration"/>: enumerating tables, or reading
/// one for its management page, is a reporting concern, so it lives behind its own interface
/// (substitutable in tests) rather than on the write service.
/// </summary>
public interface ITableDirectory
{
    /// <summary>Every table, oldest first then by label, without the join secret.</summary>
    Task<IReadOnlyList<RestaurantTableSummary>> ListTablesAsync(CancellationToken cancellationToken = default);

    /// <summary>One table by identifier, or <c>null</c> when no such table exists.</summary>
    Task<RestaurantTableSummary?> GetTableAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ITableDirectory"/>. One connection per call; columns are
/// aliased to the record's member names so Dapper maps them without <c>MatchNamesWithUnderscores</c>.
/// The <c>join_secret</c> column is never selected. The connection comes from the singleton
/// <see cref="IDatabaseConnectionFactory"/>, matching the rest of the data layer.
/// </summary>
public sealed class DapperTableDirectory : ITableDirectory
{
    private const string TableColumns = """
        restaurant_table_identifier AS TableIdentifier,
        label                       AS Label,
        is_active                   AS IsActive,
        join_secret_rotated_at      AS JoinSecretRotatedAt,
        created_at                  AS CreatedAt
        """;

    // Built from TableColumns at type-init (static readonly, not const) so the shared column list is
    // interpolated once without relying on constant-interpolated-string support.
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

        IEnumerable<RestaurantTableSummary> tables = await connection.QueryAsync<RestaurantTableSummary>(
            new CommandDefinition(ListSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return tables.ToArray();
    }

    public async Task<RestaurantTableSummary?> GetTableAsync(Guid tableIdentifier, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RestaurantTableSummary>(new CommandDefinition(
            ByIdSql,
            new { TableIdentifier = tableIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
