using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Displays;

public enum IssuePairingCodeOutcome
{
    Issued,
    TableUnavailable,
}

public sealed record IssuePairingCodeResult(IssuePairingCodeOutcome Outcome, string? Code, DateTimeOffset? ExpiresAt);

public enum RedeemPairingCodeOutcome
{
    Paired,
    CodeNotRecognized,
    TableUnavailable,
}

public sealed record RedeemPairingCodeResult(
    RedeemPairingCodeOutcome Outcome,
    Guid? DeviceIdentifier,
    Guid? TableIdentifier,
    string? TableLabel,
    string? DeviceSecret);

public enum RevokeDisplayDeviceOutcome
{
    Revoked,
    AlreadyRevoked,
    DeviceNotFound,
}

public interface IDisplayDevicePairing
{
    Task<IssuePairingCodeResult> IssuePairingCodeAsync(
        Guid tableIdentifier,
        Guid createdByPersonIdentifier,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task<RedeemPairingCodeResult> RedeemPairingCodeAsync(
        string presentedCode,
        string? deviceLabel,
        CancellationToken cancellationToken = default);

    Task<RevokeDisplayDeviceOutcome> RevokeDeviceAsync(
        Guid deviceIdentifier,
        Guid revokedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperDisplayDevicePairing : IDisplayDevicePairing
{
    private const string ActiveTableLabelSql = """
        SELECT label
        FROM restaurant_table
        WHERE restaurant_table_identifier = @TableIdentifier
          AND is_active = true;
        """;

    private const string InsertPairingCodeSql = """
        INSERT INTO table_display_pairing_code (
            table_display_pairing_code_identifier, restaurant_table_identifier, code_hash,
            created_by_person_identifier, created_at, expires_at, used_at)
        VALUES (
            @PairingCodeIdentifier, @TableIdentifier, @CodeHash,
            @CreatedBy, @CreatedAt, @ExpiresAt, NULL);
        """;

    private const string LiveCodeForUpdateSql = """
        SELECT table_display_pairing_code_identifier AS PairingCodeIdentifier,
               restaurant_table_identifier           AS TableIdentifier
        FROM table_display_pairing_code
        WHERE code_hash = @CodeHash
          AND used_at IS NULL
          AND expires_at > @Now
        FOR UPDATE;
        """;

    private const string BurnCodeSql = """
        UPDATE table_display_pairing_code
        SET used_at = @UsedAt
        WHERE table_display_pairing_code_identifier = @PairingCodeIdentifier;
        """;

    private const string InsertDeviceSql = """
        INSERT INTO table_display_device (
            table_display_device_identifier, restaurant_table_identifier, device_label,
            device_secret_hash, paired_by_person_identifier, paired_at,
            revoked_at, revoked_by_person_identifier, last_seen_at)
        VALUES (
            @DeviceIdentifier, @TableIdentifier, @DeviceLabel,
            @DeviceSecretHash, @PairedBy, @PairedAt,
            NULL, NULL, NULL);
        """;

    private const string CodePairedBySql = """
        SELECT created_by_person_identifier
        FROM table_display_pairing_code
        WHERE table_display_pairing_code_identifier = @PairingCodeIdentifier;
        """;

    private const string RevokeDeviceSql = """
        UPDATE table_display_device
        SET revoked_at = @RevokedAt,
            revoked_by_person_identifier = @RevokedBy
        WHERE table_display_device_identifier = @DeviceIdentifier
          AND revoked_at IS NULL;
        """;

    private const string DeviceExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM table_display_device
            WHERE table_display_device_identifier = @DeviceIdentifier);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperDisplayDevicePairing(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
    }

    public async Task<IssuePairingCodeResult> IssuePairingCodeAsync(
        Guid tableIdentifier,
        Guid createdByPersonIdentifier,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset expiresAt = now + lifetime;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string? tableLabel = await ReadActiveTableLabelAsync(
            connection, transaction, tableIdentifier, cancellationToken).ConfigureAwait(false);
        if (tableLabel is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new IssuePairingCodeResult(IssuePairingCodeOutcome.TableUnavailable, null, null);
        }

        string code = PairingCode.Generate();

        await connection.ExecuteAsync(new CommandDefinition(
            InsertPairingCodeSql,
            new
            {
                PairingCodeIdentifier = _identifierFactory.Create(),
                TableIdentifier = tableIdentifier,
                CodeHash = Sha256Hashing.Hash(code),
                CreatedBy = createdByPersonIdentifier,
                CreatedAt = now,
                ExpiresAt = expiresAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new IssuePairingCodeResult(IssuePairingCodeOutcome.Issued, code, expiresAt);
    }

    public async Task<RedeemPairingCodeResult> RedeemPairingCodeAsync(
        string presentedCode,
        string? deviceLabel,
        CancellationToken cancellationToken = default)
    {
        string normalized = PairingCode.Normalize(presentedCode);
        if (!PairingCode.IsWellFormed(normalized))
        {
            return NotRecognized();
        }

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        LiveCodeRow? liveCode = await connection.QuerySingleOrDefaultAsync<LiveCodeRow>(new CommandDefinition(
            LiveCodeForUpdateSql,
            new { CodeHash = Sha256Hashing.Hash(normalized), Now = now },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (liveCode is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return NotRecognized();
        }

        string? tableLabel = await ReadActiveTableLabelAsync(
            connection, transaction, liveCode.TableIdentifier, cancellationToken).ConfigureAwait(false);
        if (tableLabel is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RedeemPairingCodeResult(RedeemPairingCodeOutcome.TableUnavailable, null, null, null, null);
        }

        Guid pairedBy = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            CodePairedBySql,
            new { liveCode.PairingCodeIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Guid deviceIdentifier = _identifierFactory.Create();
        string deviceSecret = SecretGenerator.GenerateBase64UrlSecret(SecretGenerator.DeviceSecretByteCount);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertDeviceSql,
            new
            {
                DeviceIdentifier = deviceIdentifier,
                TableIdentifier = liveCode.TableIdentifier,
                DeviceLabel = ResolveDeviceLabel(deviceLabel, tableLabel),

                DeviceSecretHash = Sha256Hashing.Hash(deviceSecret),
                PairedBy = pairedBy,
                PairedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            BurnCodeSql,
            new { UsedAt = now, liveCode.PairingCodeIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RedeemPairingCodeResult(
            RedeemPairingCodeOutcome.Paired,
            deviceIdentifier,
            liveCode.TableIdentifier,
            tableLabel,
            deviceSecret);
    }

    public async Task<RevokeDisplayDeviceOutcome> RevokeDeviceAsync(
        Guid deviceIdentifier,
        Guid revokedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int affected = await connection.ExecuteAsync(new CommandDefinition(
            RevokeDeviceSql,
            new
            {
                RevokedAt = now,
                RevokedBy = revokedByPersonIdentifier,
                DeviceIdentifier = deviceIdentifier,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RevokeDisplayDeviceOutcome.Revoked;
        }

        bool exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            DeviceExistsSql,
            new { DeviceIdentifier = deviceIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return exists ? RevokeDisplayDeviceOutcome.AlreadyRevoked : RevokeDisplayDeviceOutcome.DeviceNotFound;
    }

    private static RedeemPairingCodeResult NotRecognized()
        => new(RedeemPairingCodeOutcome.CodeNotRecognized, null, null, null, null);

    private static async Task<string?> ReadActiveTableLabelAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid tableIdentifier,
        CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            ActiveTableLabelSql,
            new { TableIdentifier = tableIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static string ResolveDeviceLabel(string? requested, string tableLabel)
    {
        string trimmed = (requested ?? string.Empty).Trim();
        string label = trimmed.Length == 0 ? $"{tableLabel} display" : trimmed;
        return label.Length <= 120 ? label : label[..120];
    }

    private sealed record LiveCodeRow(Guid PairingCodeIdentifier, Guid TableIdentifier);
}
