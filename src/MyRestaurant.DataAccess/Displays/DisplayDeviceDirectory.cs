using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Displays;

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
    public bool IsRevoked => RevokedAt is not null;
}

public interface IDisplayDeviceDirectory
{
    Task<IReadOnlyList<TableDisplayDeviceSummary>> ListDevicesForTableAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default);

    Task<TableDisplayDeviceSummary?> GetDeviceAsync(
        Guid deviceIdentifier,
        CancellationToken cancellationToken = default);
}

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
