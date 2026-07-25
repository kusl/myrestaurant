using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Sittings;

/// <summary>
/// A read-only projection of a <c>table_sitting</c> row for the table surface
/// (TECHNICAL_SPECIFICATION §5.1, §5.2, §11.1). It carries the table's label so a caller can name the
/// sitting without a second lookup, and the current member count so the roster size (and the display's
/// party-size chip, §11.5) is available without loading the roster itself.
///
/// <para><see cref="ClosedAt"/> is <c>null</c> while the sitting is open; every query on this interface
/// filters to open sittings today, but the field is projected rather than dropped so the same record
/// serves the closed-sitting lookups §5.3/§11.3 add later. The settled total is deliberately absent —
/// it is a billing projection (<c>sitting_bill</c>, §8.3), not a membership fact.</para>
/// </summary>
/// <param name="SittingIdentifier">The sitting's UUIDv7 primary key (ADR-0011).</param>
/// <param name="TableIdentifier">The table the sitting is on (§4.1).</param>
/// <param name="TableLabel">That table's unique human label (e.g. "Table 5", §4.1).</param>
/// <param name="OpenedAt">When the first grant was consumed and the sitting opened (§5.1).</param>
/// <param name="ClosedAt">When the counter closed and settled it, or <c>null</c> while open (§5.3).</param>
/// <param name="MemberCount">How many people have joined this sitting (§5.1).</param>
public sealed record TableSittingSummary(
    Guid SittingIdentifier,
    Guid TableIdentifier,
    string TableLabel,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    int MemberCount)
{
    /// <summary>True while the sitting is open — nobody has closed and settled it yet (§5.3).</summary>
    public bool IsOpen => ClosedAt is null;
}

/// <summary>
/// One member of a sitting, as the party roster shows them (TECHNICAL_SPECIFICATION §5.2, §11.1).
/// Both the username and the optional display name are carried so the caller can prefer the display
/// name and still have something to render when it is absent — see <see cref="RosterName"/>.
/// </summary>
/// <param name="PersonIdentifier">The member's person id (ADR-0011).</param>
/// <param name="Username">The unique <c>citext</c> username (§3.1) — always present.</param>
/// <param name="DisplayName">The optional human display name (§3.1), or <c>null</c>.</param>
/// <param name="JoinedAt">When this person's membership row was inserted (§5.1).</param>
public sealed record SittingMemberSummary(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    DateTimeOffset JoinedAt)
{
    /// <summary>The name to show on the roster: the display name when set, otherwise the username (§5.2).</summary>
    public string RosterName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

/// <summary>
/// Reads sittings and their membership (TECHNICAL_SPECIFICATION §5.1, §5.2, §11.1). This is the
/// read-only reporting companion to <see cref="ISittingMembership"/>, mirroring how
/// <see cref="Tables.ITableDirectory"/> stands beside <see cref="Tables.ITableAdministration"/>:
/// answering "is this person already a member here?" and "who is at this table?" is a query, not part
/// of the join write path, so it lives behind its own interface and is substitutable in tests.
///
/// <para><see cref="GetOpenSittingForMemberAsync"/> is the specific query §4.4's "members bypass tokens
/// entirely" rule needs: one round trip that answers both "is there an open sitting on this table?"
/// and "is this person in it?", so the table surface can render the order view for a member without
/// looking at the query string at all.</para>
/// </summary>
public interface ISittingDirectory
{
    /// <summary>
    /// The open sitting on a table, or <c>null</c> when none is open (§5.1 — the partial unique index
    /// <c>table_sitting_one_open_per_table</c> guarantees there is at most one).
    /// </summary>
    Task<TableSittingSummary?> GetOpenSittingAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// The open sitting on a table <b>if</b> the given person is a member of it, otherwise <c>null</c>
    /// (§4.4: a member reaches the table surface regardless of any token).
    /// </summary>
    Task<TableSittingSummary?> GetOpenSittingForMemberAsync(
        Guid tableIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every open sitting the person is a member of, oldest first. A person may hold memberships in
    /// several open sittings at once (§5.1), so the <c>/table</c> index lists them and lets the person
    /// pick the one they mean.
    /// </summary>
    Task<IReadOnlyList<TableSittingSummary>> ListOpenSittingsForPersonAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>The sitting's party roster, in join order then username (§5.2).</summary>
    Task<IReadOnlyList<SittingMemberSummary>> ListMembersAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ISittingDirectory"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), and
/// columns aliased to the records' member names so Dapper maps them without
/// <c>MatchNamesWithUnderscores</c>. Every column reference is table-qualified: <c>table_sitting</c>,
/// <c>restaurant_table</c>, and <c>table_sitting_member</c> all carry same-named identifier columns, and
/// an unqualified reference across the join is exactly how PostgreSQL error 42702 (ambiguous column)
/// bites.
/// </summary>
public sealed class DapperSittingDirectory : ISittingDirectory
{
    // The member count is a correlated subquery rather than a GROUP BY so the row shape stays flat and
    // the projection is identical for every query below.
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

    // Built from the shared fragments at type-init (static readonly, not const) so the column list is
    // interpolated once without relying on constant-interpolated-string support.
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

    // Npgsql materialises a `timestamptz` column as a UTC `DateTime`, and Dapper's constructor binding
    // will not feed a `DateTime` into a `DateTimeOffset` parameter — so the rows below are read with
    // `DateTime` members that match the reader exactly, then projected to the public `DateTimeOffset`
    // records here. The stored instants are UTC, so the offset is zero (SpecifyKind guards against a
    // non-UTC Kind arriving from a future provider change).
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

    // Dapper maps these positional records by constructor-parameter name (case-insensitive) against the
    // aliased columns above; their members mirror what Npgsql returns for each column type.
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
