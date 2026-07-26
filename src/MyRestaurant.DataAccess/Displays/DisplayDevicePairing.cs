using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Displays;

/// <summary>The outcome of <see cref="IDisplayDevicePairing.IssuePairingCodeAsync"/> (§4.2).</summary>
public enum IssuePairingCodeOutcome
{
    /// <summary>A one-time code was generated, stored hashed, and returned in plaintext exactly once.</summary>
    Issued,

    /// <summary>No <b>active</b> table has that identifier, so no display can be paired to it (§4.1).</summary>
    TableUnavailable,
}

/// <summary>
/// The result of issuing a pairing code. <see cref="Code"/> is the only time the plaintext exists
/// anywhere outside the administrator's screen — the database holds only its SHA-256 hash (§4.2).
/// </summary>
/// <param name="Outcome">Whether a code was issued.</param>
/// <param name="Code">The 8-character plaintext code, or <c>null</c> when none was issued.</param>
/// <param name="ExpiresAt">When the code stops working, or <c>null</c> when none was issued.</param>
public sealed record IssuePairingCodeResult(IssuePairingCodeOutcome Outcome, string? Code, DateTimeOffset? ExpiresAt);

/// <summary>The outcome of <see cref="IDisplayDevicePairing.RedeemPairingCodeAsync"/> (§4.2).</summary>
public enum RedeemPairingCodeOutcome
{
    /// <summary>The code matched: a device row was written and its secret returned once.</summary>
    Paired,

    /// <summary>
    /// The code is not a live, unused one. Malformed, unknown, already used, and expired all collapse
    /// into this single outcome on purpose: <c>/display/pair</c> is anonymous, so telling a prober
    /// <em>which</em> of those it was would turn the page into an oracle. "Failed attempts burn nothing
    /// but the rate budget" (§4.2).
    /// </summary>
    CodeNotRecognized,

    /// <summary>
    /// The code was good but its table has since been deactivated (§4.1). Distinguished from
    /// <see cref="CodeNotRecognized"/> only for the caller's own logging — the pairing surface shows the
    /// same wording for both.
    /// </summary>
    TableUnavailable,
}

/// <summary>
/// The result of redeeming a pairing code. On success the caller must write
/// <c>device:{DeviceIdentifier}:{DeviceSecret}</c> into the device cookie (§4.2) — the plaintext secret
/// is returned exactly once and never recoverable afterwards, since only its SHA-256 hash is stored.
/// </summary>
/// <param name="Outcome">Which of the three §4.2 cases occurred.</param>
/// <param name="DeviceIdentifier">The new device's UUIDv7, or <c>null</c> when nothing was paired.</param>
/// <param name="TableIdentifier">The table it is bound to, or <c>null</c> when nothing was paired.</param>
/// <param name="TableLabel">That table's label, for the confirmation, or <c>null</c>.</param>
/// <param name="DeviceSecret">The Base64Url device secret, or <c>null</c> when nothing was paired.</param>
public sealed record RedeemPairingCodeResult(
    RedeemPairingCodeOutcome Outcome,
    Guid? DeviceIdentifier,
    Guid? TableIdentifier,
    string? TableLabel,
    string? DeviceSecret);

/// <summary>The outcome of <see cref="IDisplayDevicePairing.RevokeDeviceAsync"/> (§4.2).</summary>
public enum RevokeDisplayDeviceOutcome
{
    /// <summary><c>revoked_at</c> and <c>revoked_by_person_identifier</c> were stamped.</summary>
    Revoked,

    /// <summary>The device was already revoked; nothing changed (the first revocation stands).</summary>
    AlreadyRevoked,

    /// <summary>No device exists with that identifier; nothing changed.</summary>
    DeviceNotFound,
}

/// <summary>
/// Display-device pairing and revocation (TECHNICAL_SPECIFICATION §4.2). Three operations, all
/// administrator-driven except the middle one, which is what an unpaired screen does for itself:
///
/// <list type="bullet">
///   <item><description><b>Issue</b> — an administrator generates a one-time 8-character code for a
///   table. It is stored <b>hashed</b> (SHA-256) with an expiry and is single-use; the plaintext is
///   returned once, for a human to read off the screen and type into the device.</description></item>
///   <item><description><b>Redeem</b> — the device posts the code at the anonymous, rate-limited
///   <c>/display/pair</c>. On a match the server creates the device row with a fresh 32-byte secret,
///   marks the code used, and hands the secret back so the caller can set the cookie.</description></item>
///   <item><description><b>Revoke</b> — an administrator kills a device; its credential stops working on
///   its next request or circuit revalidation.</description></item>
/// </list>
///
/// <para>Like the other write services (<c>DapperTableAdministration</c>, <c>DapperSittingMembership</c>,
/// <c>DapperAccountAdministration</c>) this owns one connection and one transaction per operation,
/// stamps every row from a single <see cref="IClock.UtcNow"/> instant, and mints surrogate keys with the
/// application <see cref="IIdentifierFactory"/> (UUIDv7, ADR-0011). Display devices are not part of the
/// person-scoped <c>security_event</c> vocabulary (§8.2), so nothing is audited here.</para>
/// </summary>
public interface IDisplayDevicePairing
{
    /// <summary>
    /// Generates a one-time pairing code for an active table, stores its SHA-256 hash with
    /// <c>expires_at = now + <paramref name="lifetime"/></c>, and returns the plaintext once (§4.2).
    /// An unknown or deactivated table yields <see cref="IssuePairingCodeOutcome.TableUnavailable"/> and
    /// writes nothing (§4.1).
    /// </summary>
    Task<IssuePairingCodeResult> IssuePairingCodeAsync(
        Guid tableIdentifier,
        Guid createdByPersonIdentifier,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a presented pairing code for a new display device bound to that code's table (§4.2).
    /// The code is normalized (case and separators) before lookup, checked unused and unexpired
    /// <b>under a row lock</b> so two devices racing the same code cannot both pair, then burnt. A blank
    /// <paramref name="deviceLabel"/> is replaced with a label derived from the table.
    /// </summary>
    Task<RedeemPairingCodeResult> RedeemPairingCodeAsync(
        string presentedCode,
        string? deviceLabel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a display device (§4.2). Idempotent in effect: a second call reports
    /// <see cref="RevokeDisplayDeviceOutcome.AlreadyRevoked"/> and leaves the original revocation
    /// timestamp and actor intact.
    /// </summary>
    Task<RevokeDisplayDeviceOutcome> RevokeDeviceAsync(
        Guid deviceIdentifier,
        Guid revokedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDisplayDevicePairing" />
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

    // FOR UPDATE serializes two devices racing the same code: the loser reads used_at as stamped.
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

        // Plaintext lives only in this local and the caller's one-time render; the row gets the hash.
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
        // Cheap, database-free rejection of anything that could not possibly be a code (§4.2). A code
        // typed with the separators a human naturally adds still normalizes to a well-formed one.
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

        // The code IS the lookup key here (we hold no candidate row to compare against), so a hash
        // equality predicate is the right shape; there is no secret-dependent branch to time.
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

        // Re-checked under the code's row lock: a table deactivated between issue and redeem takes no
        // new displays (§4.1).
        string? tableLabel = await ReadActiveTableLabelAsync(
            connection, transaction, liveCode.TableIdentifier, cancellationToken).ConfigureAwait(false);
        if (tableLabel is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RedeemPairingCodeResult(RedeemPairingCodeOutcome.TableUnavailable, null, null, null, null);
        }

        // The administrator who issued the code is recorded as the pairer: they are the person who
        // authorized this screen, and paired_by_person_identifier is NOT NULL with no anonymous option.
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

                // sha256 of the Base64Url TEXT that travels in the cookie (§4.2), so validation never
                // has to decode it — and the 32-byte digest satisfies the column's octet_length CHECK.
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

        // One conditional UPDATE; the affected-row count tells revoked-now from already-revoked once we
        // know the row exists at all. The revoked_at / revoked_by CHECK is satisfied by setting both.
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

    /// <summary>The label of the table if it exists and is active (§4.1), otherwise <c>null</c>.</summary>
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

    /// <summary>
    /// <c>device_label</c> is NOT NULL and is what an administrator reads in the devices list, so a blank
    /// one becomes something identifiable rather than an empty cell. Length is bounded so a pasted essay
    /// cannot bloat the row.
    /// </summary>
    private static string ResolveDeviceLabel(string? requested, string tableLabel)
    {
        string trimmed = (requested ?? string.Empty).Trim();
        string label = trimmed.Length == 0 ? $"{tableLabel} display" : trimmed;
        return label.Length <= 120 ? label : label[..120];
    }

    // Dapper maps this positional record by constructor-parameter name against the aliased columns above.
    private sealed record LiveCodeRow(Guid PairingCodeIdentifier, Guid TableIdentifier);
}
