using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Displays;

/// <summary>
/// A live display device, as the request pipeline needs it (TECHNICAL_SPECIFICATION §4.2, §11.5): who
/// the device is, which table it may show, and whether that table is still in service. The secret hash
/// is deliberately absent — it never leaves <see cref="DapperDisplayDeviceAuthenticator"/>.
/// </summary>
/// <param name="DeviceIdentifier">The device's UUIDv7 primary key (ADR-0011).</param>
/// <param name="TableIdentifier">The one table this display is bound to — its "table claim" (§3.7).</param>
/// <param name="DeviceLabel">The human label given at pairing.</param>
/// <param name="TableLabel">That table's unique human label, for the full-screen heading (§11.5).</param>
/// <param name="TableIsActive">
/// False once the table is deactivated. The device is <b>still</b> authenticated — its credential is
/// perfectly good — but §4.1 stops display rendering for that table, so the surface shows an
/// out-of-service state instead of a QR rather than throwing the screen back to pairing.
/// </param>
public sealed record DisplayDeviceSession(
    Guid DeviceIdentifier,
    Guid TableIdentifier,
    string DeviceLabel,
    string TableLabel,
    bool TableIsActive);

/// <summary>
/// Authenticates a display device from its cookie credential (TECHNICAL_SPECIFICATION §4.2). §4.2 is
/// explicit about the shape: the cookie carries <c>device:{device_identifier}:{secret}</c>, the server
/// stores only <c>sha256(secret)</c>, and "each request re-validates the hash and
/// <c>revoked_at IS NULL</c>; <c>last_seen_at</c> is updated at most once per minute".
///
/// <para>Two entry points, because a display lives in two phases. <see cref="AuthenticateAsync"/> is the
/// full check an HTTP request performs: identifier plus secret, compared in constant time. Once a Blazor
/// circuit is established the cookie is out of reach (a circuit cannot read cookies), so
/// <see cref="RevalidateAsync"/> re-checks the identifier alone — which is exactly what §4.2's "or
/// circuit revalidation" needs it to catch: revocation, and a table going out of service. It is not a
/// re-authentication and must never be reachable from a request that has not already presented the
/// secret.</para>
///
/// <para>Both paths touch <c>last_seen_at</c> through the same rate-limited UPDATE, so a display that is
/// simply sitting on its circuit still reports a heartbeat every window without a write per request.</para>
/// </summary>
public interface IDisplayDeviceAuthenticator
{
    /// <summary>
    /// The session for a non-revoked device whose stored hash matches <paramref name="presentedSecret"/>,
    /// or <c>null</c> when the device is unknown, revoked, or the secret does not match (§4.2).
    /// </summary>
    Task<DisplayDeviceSession?> AuthenticateAsync(
        Guid deviceIdentifier,
        string presentedSecret,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The session for a device already authenticated earlier in this circuit's life, or <c>null</c>
    /// once it has been revoked or deleted (§4.2 "circuit revalidation"). Does <b>not</b> check the
    /// secret — the caller must have proven it on the request that established the circuit.
    /// </summary>
    Task<DisplayDeviceSession?> RevalidateAsync(
        Guid deviceIdentifier,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDisplayDeviceAuthenticator" />
public sealed class DapperDisplayDeviceAuthenticator : IDisplayDeviceAuthenticator
{
    /// <summary>§4.2: <c>last_seen_at</c> is updated at most once per minute.</summary>
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

    // One statement carries the whole "at most once per minute" rule: the row only moves when the last
    // recorded sighting is older than the resolution, so no read-then-write race can double-write.
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
            return null; // unknown or revoked — the same nothing, deliberately
        }

        // Constant-time, per §3.4/§4.2: this is a comparison against a stored hash for a known row, so
        // a short-circuiting byte compare would leak the prefix over enough attempts.
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

    // Dapper maps this positional record by constructor-parameter name (case-insensitive) against the
    // aliased columns above; its members mirror what Npgsql returns for each column type.
    private sealed record DisplayDeviceRow(
        Guid DeviceIdentifier,
        Guid TableIdentifier,
        string DeviceLabel,
        byte[] DeviceSecretHash,
        string TableLabel,
        bool TableIsActive);
}
