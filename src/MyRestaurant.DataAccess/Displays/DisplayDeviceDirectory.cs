using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Displays;

/// <summary>
/// A read-only projection of a <c>table_display_device</c> row for the administration tables area
/// (TECHNICAL_SPECIFICATION §4.2, §11.4). It deliberately omits <c>device_secret_hash</c> — that column
/// exists only to be compared against a presented cookie secret, never to be shown — and resolves both
/// person references to usernames so the administration page can name who paired and who revoked a
/// display without a second lookup.
/// </summary>
/// <param name="DeviceIdentifier">The device's UUIDv7 primary key (ADR-0011).</param>
/// <param name="TableIdentifier">The one table this display is bound to (§4.2).</param>
/// <param name="DeviceLabel">The human label given at pairing ("Table 4 — window tablet").</param>
/// <param name="PairedByUsername">The administrator who generated the pairing code's username (§4.2).</param>
/// <param name="PairedAt">When the device redeemed its pairing code.</param>
/// <param name="RevokedAt">When the device was revoked, or <c>null</c> while it is live (§4.2).</param>
/// <param name="RevokedByUsername">Who revoked it, or <c>null</c> while it is live.</param>
/// <param name="LastSeenAt">
/// The most recent request the device made, refreshed at most once per minute (§4.2), or <c>null</c>
/// if it has not been seen since pairing.
/// </param>
public sealed record TableDisplayDeviceSummary(
    Guid DeviceIdentifier,
    Guid TableIdentifier,
    string DeviceLabel,
    string PairedByUsername,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedByUsername,
    DateTimeOffset? LastSeenAt)
{
    /// <summary>True once revoked — the credential dies on the device's next request (§4.2).</summary>
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// Reads display devices for the administration area (TECHNICAL_SPECIFICATION §4.2, §11.4). This is the
/// read-only reporting companion to <see cref="IDisplayDevicePairing"/>, mirroring how
/// <see cref="Tables.ITableDirectory"/> stands beside <see cref="Tables.ITableAdministration"/> and how
/// <see cref="Sittings.ISittingDirectory"/> stands beside <see cref="Sittings.ISittingMembership"/>:
/// listing devices is a query, not part of the pairing write path, so it lives behind its own interface
/// and is substitutable in tests.
/// </summary>
public interface IDisplayDeviceDirectory
{
    /// <summary>
    /// Every display device ever paired to a table, live ones first then newest first. Revoked devices
    /// are kept and listed because deletion does not exist here either (F-10b): the history of which
    /// screen showed which table's code is worth keeping.
    /// </summary>
    Task<IReadOnlyList<TableDisplayDeviceSummary>> ListDevicesForTableAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>One device by identifier, or <c>null</c> when no such device exists.</summary>
    Task<TableDisplayDeviceSummary?> GetDeviceAsync(
        Guid deviceIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IDisplayDeviceDirectory"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), and columns
/// aliased to the record's member names so Dapper maps them without <c>MatchNamesWithUnderscores</c>.
/// Every column reference is table-qualified: <c>table_display_device</c> is joined to <c>person</c>
/// twice (the pairer and the revoker) and both carry a <c>person_identifier</c>, which is exactly how
/// PostgreSQL error 42702 (ambiguous column) bites.
/// </summary>
public sealed class DapperDisplayDeviceDirectory : IDisplayDeviceDirectory
{
    private const string DeviceColumns = """
        table_display_device.table_display_device_identifier AS DeviceIdentifier,
        table_display_device.restaurant_table_identifier     AS TableIdentifier,
        table_display_device.device_label                    AS DeviceLabel,
        paired_by.username                                   AS PairedByUsername,
        table_display_device.paired_at                       AS PairedAt,
        table_display_device.revoked_at                      AS RevokedAt,
        revoked_by.username                                  AS RevokedByUsername,
        table_display_device.last_seen_at                    AS LastSeenAt
        """;

    private const string DeviceFrom = """
        FROM table_display_device
        INNER JOIN person AS paired_by
                ON paired_by.person_identifier = table_display_device.paired_by_person_identifier
        LEFT JOIN person AS revoked_by
                ON revoked_by.person_identifier = table_display_device.revoked_by_person_identifier
        """;

    // Built from the shared fragments at type-init (static readonly, not const) so the column list is
    // interpolated once without relying on constant-interpolated-string support.
    // `(revoked_at IS NOT NULL)` sorts false before true, so live devices lead.
    private static readonly string ListForTableSql = $"""
        SELECT {DeviceColumns}
        {DeviceFrom}
        WHERE table_display_device.restaurant_table_identifier = @TableIdentifier
        ORDER BY (table_display_device.revoked_at IS NOT NULL), table_display_device.paired_at DESC;
        """;

    private static readonly string ByIdSql = $"""
        SELECT {DeviceColumns}
        {DeviceFrom}
        WHERE table_display_device.table_display_device_identifier = @DeviceIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperDisplayDeviceDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<TableDisplayDeviceSummary>> ListDevicesForTableAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<DisplayDeviceRow> rows = await connection.QueryAsync<DisplayDeviceRow>(new CommandDefinition(
            ListForTableSql,
            new { TableIdentifier = tableIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<TableDisplayDeviceSummary?> GetDeviceAsync(
        Guid deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        DisplayDeviceRow? row = await connection.QuerySingleOrDefaultAsync<DisplayDeviceRow>(new CommandDefinition(
            ByIdSql,
            new { DeviceIdentifier = deviceIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    // Npgsql materialises a `timestamptz` column as a UTC `DateTime`, and Dapper's constructor binding
    // will not feed a `DateTime` into a `DateTimeOffset` parameter — so the row is read with `DateTime`
    // members that match the reader exactly, then projected to the public `DateTimeOffset` summary. The
    // stored instants are UTC, so the offset is zero (SpecifyKind guards against a non-UTC Kind arriving
    // from a future provider change). Same fix as TableDirectory, PersonDirectory, and SittingDirectory.
    private static TableDisplayDeviceSummary ToSummary(DisplayDeviceRow row) => new(
        row.DeviceIdentifier,
        row.TableIdentifier,
        row.DeviceLabel,
        row.PairedByUsername,
        AsUtc(row.PairedAt),
        row.RevokedAt is { } revokedAt ? AsUtc(revokedAt) : null,
        row.RevokedByUsername,
        row.LastSeenAt is { } lastSeenAt ? AsUtc(lastSeenAt) : null);

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Dapper maps this positional record by constructor-parameter name (case-insensitive) against the
    // aliased columns above; its members mirror what Npgsql returns for each column type.
    private sealed record DisplayDeviceRow(
        Guid DeviceIdentifier,
        Guid TableIdentifier,
        string DeviceLabel,
        string PairedByUsername,
        DateTime PairedAt,
        DateTime? RevokedAt,
        string? RevokedByUsername,
        DateTime? LastSeenAt);
}
