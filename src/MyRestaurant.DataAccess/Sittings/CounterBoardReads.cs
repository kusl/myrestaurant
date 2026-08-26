using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Sittings;

public sealed record CounterSittingSummary(
    Guid SittingIdentifier,
    Guid TableIdentifier,
    string TableLabel,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    Guid? ClosedByPersonIdentifier,
    string? ClosedByName,
    decimal? SettledTotalAmount,
    int MemberCount,
    int OrderCount,
    int PendingLineCount,
    int FulfilledLineCount,
    decimal CurrentTotalAmount,
    DateTimeOffset? LastEventAt)
{
    public bool IsOpen => ClosedAt is null;

    public bool HasPendingLines => PendingLineCount > 0;

    public bool HasPostCloseCorrections
        => SettledTotalAmount is { } settled && settled != CurrentTotalAmount;

    public decimal AmountToShow => SettledTotalAmount ?? CurrentTotalAmount;
}

public interface ICounterBoardReads
{
    Task<IReadOnlyList<CounterSittingSummary>> ListOpenSittingsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CounterSittingSummary>> ListRecentlyClosedSittingsAsync(
        DateTimeOffset closedSince,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<CounterSittingSummary?> GetSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperCounterBoardReads : ICounterBoardReads
{
    private const string SittingColumns = """
        table_sitting.table_sitting_identifier      AS SittingIdentifier,
        table_sitting.restaurant_table_identifier   AS TableIdentifier,
        restaurant_table.label                      AS TableLabel,
        table_sitting.opened_at                     AS OpenedAt,
        table_sitting.closed_at                     AS ClosedAt,
        table_sitting.closed_by_person_identifier   AS ClosedByPersonIdentifier,
        COALESCE(NULLIF(btrim(closed_by.display_name), ''), closed_by.username)
                                                    AS ClosedByName,
        table_sitting.settled_total_amount          AS SettledTotalAmount,
        (SELECT count(*)
         FROM table_sitting_member
         WHERE table_sitting_member.table_sitting_identifier = table_sitting.table_sitting_identifier)::int
                                                    AS MemberCount,
        totals.order_count                          AS OrderCount,
        totals.pending_line_count                   AS PendingLineCount,
        totals.fulfilled_line_count                 AS FulfilledLineCount,
        totals.current_total_amount                 AS CurrentTotalAmount,
        totals.last_event_at                        AS LastEventAt
        """;

    private const string SittingFrom = """
        FROM table_sitting
        INNER JOIN restaurant_table
                ON restaurant_table.restaurant_table_identifier = table_sitting.restaurant_table_identifier
        LEFT JOIN person AS closed_by
               ON closed_by.person_identifier = table_sitting.closed_by_person_identifier
        LEFT JOIN LATERAL (
            SELECT count(*)::int                                              AS order_count,
                   COALESCE(sum(state.pending_line_count), 0)::int            AS pending_line_count,
                   COALESCE(sum(state.fulfilled_line_count), 0)::int          AS fulfilled_line_count,
                   COALESCE(sum(state.current_total_amount), 0)::numeric(10,2) AS current_total_amount,
                   max(state.last_event_at)                                   AS last_event_at
            FROM order_current_state AS state
            WHERE state.table_sitting_identifier = table_sitting.table_sitting_identifier
        ) AS totals ON true
        """;

    private static readonly string OpenSittingsSql = $"""
        SELECT {SittingColumns}
        {SittingFrom}
        WHERE table_sitting.closed_at IS NULL
        ORDER BY table_sitting.opened_at, restaurant_table.label;
        """;

    private static readonly string RecentlyClosedSittingsSql = $"""
        SELECT {SittingColumns}
        {SittingFrom}
        WHERE table_sitting.closed_at IS NOT NULL
          AND table_sitting.closed_at >= @ClosedSince
        ORDER BY table_sitting.closed_at DESC, restaurant_table.label
        LIMIT @MaximumCount;
        """;

    private static readonly string SittingByIdentifierSql = $"""
        SELECT {SittingColumns}
        {SittingFrom}
        WHERE table_sitting.table_sitting_identifier = @SittingIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperCounterBoardReads(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<CounterSittingSummary>> ListOpenSittingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<CounterSittingRow> rows = await connection
            .QueryAsync<CounterSittingRow>(new CommandDefinition(
                OpenSittingsSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<IReadOnlyList<CounterSittingSummary>> ListRecentlyClosedSittingsAsync(
        DateTimeOffset closedSince,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<CounterSittingRow> rows = await connection
            .QueryAsync<CounterSittingRow>(new CommandDefinition(
                RecentlyClosedSittingsSql,
                new { ClosedSince = closedSince, MaximumCount = maximumCount },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<CounterSittingSummary?> GetSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        CounterSittingRow? row = await connection
            .QuerySingleOrDefaultAsync<CounterSittingRow>(new CommandDefinition(
                SittingByIdentifierSql,
                new { SittingIdentifier = sittingIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    private static CounterSittingSummary ToSummary(CounterSittingRow row) => new(
        row.SittingIdentifier,
        row.TableIdentifier,
        row.TableLabel,
        AsUtc(row.OpenedAt),
        row.ClosedAt is { } closedAt ? AsUtc(closedAt) : null,
        row.ClosedByPersonIdentifier,
        row.ClosedByName,
        row.SettledTotalAmount,
        row.MemberCount,
        row.OrderCount,
        row.PendingLineCount,
        row.FulfilledLineCount,
        row.CurrentTotalAmount,
        row.LastEventAt is { } lastEventAt ? AsUtc(lastEventAt) : null);

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record CounterSittingRow(
        Guid SittingIdentifier,
        Guid TableIdentifier,
        string TableLabel,
        DateTime OpenedAt,
        DateTime? ClosedAt,
        Guid? ClosedByPersonIdentifier,
        string? ClosedByName,
        decimal? SettledTotalAmount,
        int MemberCount,
        int OrderCount,
        int PendingLineCount,
        int FulfilledLineCount,
        decimal CurrentTotalAmount,
        DateTime? LastEventAt);
}
