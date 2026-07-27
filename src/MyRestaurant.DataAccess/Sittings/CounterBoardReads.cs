using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Sittings;

/// <summary>
/// One sitting as the counter sees it (TECHNICAL_SPECIFICATION §11.3): the table it is on, how many
/// people are at it, how much is on it, how much of that has not reached the table yet, and — once it
/// is closed — what was stamped and what has happened since.
///
/// <para>This is deliberately wider than <see cref="TableSittingSummary"/>, which answers the join
/// flow's question ("is there an open sitting here, and am I in it?"). The counter's question is a
/// billing one, so the money and the line counts are folded in from <c>order_current_state</c> rather
/// than left for the caller to fetch per row — twenty open tables would otherwise be twenty extra round
/// trips on a screen that re-reads itself on every §9 notification.</para>
///
/// <para>Both totals are carried on purpose. §5.3: <c>settled_total_amount</c> "is <b>never
/// rewritten</b>; post-close corrections (§6.7) live beside it, and the UI shows both the stamped
/// settled total and, when corrective events exist, the current corrected total".
/// <see cref="HasPostCloseCorrections"/> is that comparison, made once, here.</para>
/// </summary>
/// <param name="SittingIdentifier">The sitting's UUIDv7 primary key (ADR-0011).</param>
/// <param name="TableIdentifier">The table it is on (§4.1).</param>
/// <param name="TableLabel">That table's unique human label.</param>
/// <param name="OpenedAt">When the first grant was consumed (§5.1).</param>
/// <param name="ClosedAt">When it was closed and settled, or <c>null</c> while open (§5.3).</param>
/// <param name="ClosedByPersonIdentifier">Who closed it, or <c>null</c> while open.</param>
/// <param name="ClosedByName">That person's display name, falling back to their username; <c>null</c> while open.</param>
/// <param name="SettledTotalAmount">The total stamped at close and never rewritten; <c>null</c> while open (§5.3).</param>
/// <param name="MemberCount">How many people have joined (§5.1) — including any who never ordered.</param>
/// <param name="OrderCount">How many living orders exist (§6.1) — people who have actually sent something.</param>
/// <param name="PendingLineCount">Current lines not yet fulfilled — §5.3's pre-close warning.</param>
/// <param name="FulfilledLineCount">Current lines the kitchen has sent out.</param>
/// <param name="CurrentTotalAmount">The sum over <c>sitting_bill</c> right now, pending lines included (§8.3).</param>
/// <param name="LastEventAt">The most recent order event anywhere in the sitting — §5.4's "last-activity timestamps"; <c>null</c> when nothing has been ordered.</param>
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
    /// <summary>True while nobody has closed and settled it (§5.3).</summary>
    public bool IsOpen => ClosedAt is null;

    /// <summary>True when something on this table has not reached it yet — what §5.3 warns about before Close.</summary>
    public bool HasPendingLines => PendingLineCount > 0;

    /// <summary>
    /// True when the sitting is closed and the current total no longer equals the total stamped at
    /// close — that is, when §6.7 corrective events exist. §5.3 requires both numbers to be shown when
    /// this holds.
    /// </summary>
    public bool HasPostCloseCorrections
        => SettledTotalAmount is { } settled && settled != CurrentTotalAmount;

    /// <summary>The amount actually owed as of now: the live total while open, the stamped one once closed.</summary>
    public decimal AmountToShow => SettledTotalAmount ?? CurrentTotalAmount;
}

/// <summary>
/// The counter's reads (TECHNICAL_SPECIFICATION §11.3, §5.4). Three questions the four §8.3 projection
/// views cannot answer on their own, because all four are scoped to an order or a sitting the caller
/// already knows: "which tables are open right now", "which have just been settled", and "what is the
/// state of this one".
///
/// <para>Separate from <see cref="ISittingDirectory"/> for the same reason
/// <see cref="Orders.IKitchenBoardReads"/> is separate from <see cref="Orders.IOrderReadModel"/>: the
/// directory exists to answer the join flow's membership question and is consumed by the guest surface,
/// while this rolls money and line counts across a whole sitting for a staff screen. Widening the
/// directory's record would put a billing projection into the type the table surface renders a roster
/// from.</para>
/// </summary>
public interface ICounterBoardReads
{
    /// <summary>
    /// Every open sitting in the restaurant, oldest first then by table label — the counter's landing
    /// list (§11.3). Oldest first because the table that has been sitting longest is the one most likely
    /// to be asking for its bill.
    /// </summary>
    Task<IReadOnlyList<CounterSittingSummary>> ListOpenSittingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sittings closed at or after <paramref name="closedSince"/>, most recently closed first, capped at
    /// <paramref name="maximumCount"/> — §11.3's read-only "closed-sitting lookup". Bounded on both axes
    /// deliberately: this is the "what did we just settle?" list a counter checks a receipt against, not
    /// an archive. The archive is administration's (§11.4).
    /// </summary>
    Task<IReadOnlyList<CounterSittingSummary>> ListRecentlyClosedSittingsAsync(
        DateTimeOffset closedSince,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>One sitting, open or closed, or <c>null</c> when no sitting has that identifier — the drill-in header (§11.3).</summary>
    Task<CounterSittingSummary?> GetSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ICounterBoardReads"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns
/// aliased to the record's member names, every column reference table-qualified (three of these tables
/// carry same-named identifier columns and an unqualified reference is exactly how PostgreSQL error
/// 42702 bites), and rows read into an internal row type with <see cref="DateTime"/> members before
/// being projected — the same Npgsql/Dapper constructor-binding fix every other reader here carries.
/// </summary>
public sealed class DapperCounterBoardReads : ICounterBoardReads
{
    /// <summary>
    /// The aggregate is a LATERAL rather than a GROUP BY so the row shape stays flat and identical for
    /// all three queries. Every cast is deliberate: <c>count(*)</c> and the view's line counts are
    /// <c>bigint</c>, and <c>sum()</c> over them widens to <c>numeric</c> — neither of which Dapper's
    /// constructor binding will feed into an <see cref="int"/> parameter, so they are narrowed in SQL
    /// where the intent is visible rather than converted in C# where it would not be. An aggregate with
    /// no GROUP BY always returns exactly one row, so the sitting appears even when nobody has ordered.
    /// </summary>
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

    // Built at type-init (static readonly, not const) so the shared fragments interpolate once.
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
        // A non-positive cap would make LIMIT 0 (or an error); asking for nothing is answered without a
        // round trip rather than by an exception a caller has to defend against.
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
