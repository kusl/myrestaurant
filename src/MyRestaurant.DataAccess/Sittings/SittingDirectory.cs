using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Sittings;

public sealed record TableSittingSummary(
    Guid SittingIdentifier,
    Guid TableIdentifier,
    string TableLabel,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    int MemberCount)
{
    public bool IsOpen => ClosedAt is null;
}

public sealed record SittingMemberSummary(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    DateTimeOffset JoinedAt)
{
    public string RosterName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

public interface ISittingDirectory
{
    Task<TableSittingSummary?> GetOpenSittingAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);

    Task<TableSittingSummary?> GetOpenSittingForMemberAsync(
        Guid tableIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TableSittingSummary>> ListOpenSittingsForPersonAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SittingMemberSummary>> ListMembersAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperSittingDirectory : ISittingDirectory
{
    private const string SittingColumns = """
        table_sitting.table_sitting_identifier    AS SittingIdentifier,
        table_sitting.restaurant_table_identifier AS TableIdentifier,
        restaurant_table.label                    AS TableLabel,
        table_sitting.opened_at                   AS OpenedAt,
        table_sitting.closed_at                   AS ClosedAt,
        (SELECT count(*)
         FROM table_sitting_member
         WHERE table_sitting_member.table_sitting_identifier = table_sitting.table_sitting_identifier)::int
                                                  AS MemberCount
        """;

    private const string SittingFrom = """
        FROM table_sitting
        INNER JOIN restaurant_table
                ON restaurant_table.restaurant_table_identifier = table_sitting.restaurant_table_identifier
        """;

    private static readonly string OpenSittingByTableSql = $"""
        SELECT {SittingColumns}
        {SittingFrom}
        WHERE table_sitting.restaurant_table_identifier = @TableIdentifier
          AND table_sitting.closed_at IS NULL;
        """;

    private static readonly string OpenSittingForMemberSql = $"""
        SELECT {SittingColumns}
        {SittingFrom}
        WHERE table_sitting.restaurant_table_identifier = @TableIdentifier
          AND table_sitting.closed_at IS NULL
          AND EXISTS (
              SELECT 1
              FROM table_sitting_member
              WHERE table_sitting_member.table_sitting_identifier = table_sitting.table_sitting_identifier
                AND table_sitting_member.person_identifier = @PersonIdentifier);
        """;

    private static readonly string OpenSittingsForPersonSql = $"""
        SELECT {SittingColumns}
        {SittingFrom}
        WHERE table_sitting.closed_at IS NULL
          AND EXISTS (
              SELECT 1
              FROM table_sitting_member
              WHERE table_sitting_member.table_sitting_identifier = table_sitting.table_sitting_identifier
                AND table_sitting_member.person_identifier = @PersonIdentifier)
        ORDER BY table_sitting.opened_at, restaurant_table.label;
        """;

    private const string MembersSql = """
        SELECT person.person_identifier        AS PersonIdentifier,
               person.username                 AS Username,
               person.display_name             AS DisplayName,
               table_sitting_member.joined_at  AS JoinedAt
        FROM table_sitting_member
        INNER JOIN person
                ON person.person_identifier = table_sitting_member.person_identifier
        WHERE table_sitting_member.table_sitting_identifier = @SittingIdentifier
        ORDER BY table_sitting_member.joined_at, person.username;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperSittingDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<TableSittingSummary?> GetOpenSittingAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        TableSittingRow? row = await connection.QuerySingleOrDefaultAsync<TableSittingRow>(new CommandDefinition(
            OpenSittingByTableSql,
            new { TableIdentifier = tableIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    public async Task<TableSittingSummary?> GetOpenSittingForMemberAsync(
        Guid tableIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        TableSittingRow? row = await connection.QuerySingleOrDefaultAsync<TableSittingRow>(new CommandDefinition(
            OpenSittingForMemberSql,
            new { TableIdentifier = tableIdentifier, PersonIdentifier = personIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    public async Task<IReadOnlyList<TableSittingSummary>> ListOpenSittingsForPersonAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<TableSittingRow> rows = await connection.QueryAsync<TableSittingRow>(new CommandDefinition(
            OpenSittingsForPersonSql,
            new { PersonIdentifier = personIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<IReadOnlyList<SittingMemberSummary>> ListMembersAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SittingMemberRow> rows = await connection.QueryAsync<SittingMemberRow>(new CommandDefinition(
            MembersSql,
            new { SittingIdentifier = sittingIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    private static TableSittingSummary ToSummary(TableSittingRow row) => new(
        row.SittingIdentifier,
        row.TableIdentifier,
        row.TableLabel,
        AsUtc(row.OpenedAt),
        row.ClosedAt is { } closedAt ? AsUtc(closedAt) : null,
        row.MemberCount);

    private static SittingMemberSummary ToSummary(SittingMemberRow row) => new(
        row.PersonIdentifier,
        row.Username,
        row.DisplayName,
        AsUtc(row.JoinedAt));

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record TableSittingRow(
        Guid SittingIdentifier,
        Guid TableIdentifier,
        string TableLabel,
        DateTime OpenedAt,
        DateTime? ClosedAt,
        int MemberCount);

    private sealed record SittingMemberRow(
        Guid PersonIdentifier,
        string Username,
        string? DisplayName,
        DateTime JoinedAt);
}
