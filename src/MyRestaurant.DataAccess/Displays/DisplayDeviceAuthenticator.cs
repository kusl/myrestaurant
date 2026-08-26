using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Displays;

public sealed record DisplayDeviceSession(
    Guid DeviceIdentifier,
    Guid TableIdentifier,
    string DeviceLabel,
    string TableLabel,
    bool TableIsActive);

public interface IDisplayDeviceAuthenticator
{
    Task<DisplayDeviceSession?> AuthenticateAsync(
        Guid deviceIdentifier,
        string presentedSecret,
        CancellationToken cancellationToken = default);

    Task<DisplayDeviceSession?> RevalidateAsync(
        Guid deviceIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperDisplayDeviceAuthenticator : IDisplayDeviceAuthenticator
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    private const string LiveDeviceSql = """
        SELECT table_display_device.table_display_device_identifier AS DeviceIdentifier,
               table_display_device.restaurant_table_identifier     AS TableIdentifier,
               table_display_device.device_label                    AS DeviceLabel,
               table_display_device.device_secret_hash              AS DeviceSecretHash,
               restaurant_table.label                               AS TableLabel,
               restaurant_table.is_active                           AS TableIsActive
        FROM table_display_device
        INNER JOIN restaurant_table
                ON restaurant_table.restaurant_table_identifier = table_display_device.restaurant_table_identifier
        WHERE table_display_device.table_display_device_identifier = @DeviceIdentifier
          AND table_display_device.revoked_at IS NULL;
        """;

    private const string TouchLastSeenSql = """
        UPDATE table_display_device
        SET last_seen_at = @Now
        WHERE table_display_device_identifier = @DeviceIdentifier
          AND (last_seen_at IS NULL OR last_seen_at < @Threshold);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public DapperDisplayDeviceAuthenticator(IDatabaseConnectionFactory connectionFactory, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);

        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public Task<DisplayDeviceSession?> AuthenticateAsync(
        Guid deviceIdentifier,
        string presentedSecret,
        CancellationToken cancellationToken = default)
        => ResolveAsync(deviceIdentifier, presentedSecret, requireSecret: true, cancellationToken);

    public Task<DisplayDeviceSession?> RevalidateAsync(
        Guid deviceIdentifier,
        CancellationToken cancellationToken = default)
        => ResolveAsync(deviceIdentifier, presentedSecret: null, requireSecret: false, cancellationToken);

    private async Task<DisplayDeviceSession?> ResolveAsync(
        Guid deviceIdentifier,
        string? presentedSecret,
        bool requireSecret,
        CancellationToken cancellationToken)
    {
        if (deviceIdentifier == Guid.Empty || (requireSecret && string.IsNullOrEmpty(presentedSecret)))
        {
            return null;
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        DisplayDeviceRow? row = await connection.QuerySingleOrDefaultAsync<DisplayDeviceRow>(new CommandDefinition(
            LiveDeviceSql,
            new { DeviceIdentifier = deviceIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        if (requireSecret && !Sha256Hashing.MatchesStoredHash(presentedSecret!, row.DeviceSecretHash))
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            TouchLastSeenSql,
            new
            {
                Now = now,
                Threshold = now - LastSeenResolution,
                DeviceIdentifier = deviceIdentifier,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new DisplayDeviceSession(
            row.DeviceIdentifier,
            row.TableIdentifier,
            row.DeviceLabel,
            row.TableLabel,
            row.TableIsActive);
    }

    private sealed record DisplayDeviceRow(
        Guid DeviceIdentifier,
        Guid TableIdentifier,
        string DeviceLabel,
        byte[] DeviceSecretHash,
        string TableLabel,
        bool TableIsActive);
}
