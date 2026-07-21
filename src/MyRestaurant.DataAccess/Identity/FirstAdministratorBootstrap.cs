using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// The identity the <c>/setup</c> wizard has fully assembled — a username/display name, an
/// already-Argon2id-hashed password (§3.2), a confirmed passkey, and a confirmed TOTP secret — ready
/// to become the first administrator. Nothing here is persisted yet: the wizard verifies the passkey
/// (WebAuthn attestation) and the TOTP code across earlier requests and carries the results forward,
/// so <see cref="IFirstAdministratorBootstrap.CreateFirstAdministratorAsync"/> can write the whole
/// account in one transaction (TECHNICAL_SPECIFICATION §3.6).
/// </summary>
/// <param name="PersonIdentifier">
/// The person's UUIDv7, minted by the wizard at its first step so it can double as the stable
/// WebAuthn user handle for the passkey ceremony — it must equal the eventual <c>person</c> row's id
/// (the authenticator returns it on a discoverable sign-in, and the framework matches it).
/// </param>
/// <param name="Username">The unique <c>citext</c> username, 3–64 characters (§3.1).</param>
/// <param name="DisplayName">Optional display name shown on rosters and the kitchen queue.</param>
/// <param name="PasswordHash">The Argon2id PHC string (§3.2); the wizard hashed the plaintext already.</param>
/// <param name="TotpSecretBase32">
/// The confirmed authenticator secret, Base32, in the clear — this class protects it with ASP.NET
/// Data Protection under the same purpose the store reads it back with (§3.4).
/// </param>
/// <param name="Passkey">
/// The verified attestation result. Only its immutable/registration fields are used; the row's
/// <c>created_at</c> is stamped by this class so every inserted row shares one instant.
/// </param>
public sealed record NewAdministrator(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    string PasswordHash,
    string TotpSecretBase32,
    UserPasskeyInfo Passkey);

/// <summary>The outcome of attempting the bootstrap.</summary>
public enum FirstAdministratorBootstrapStatus
{
    /// <summary>The account was created and granted <c>administrator</c> in one transaction.</summary>
    Created,

    /// <summary>
    /// An administrator already existed when the transaction took the lock, so nothing was written —
    /// the wizard must now behave as if <c>/setup</c> is gone (return 404). This is the losing side of
    /// a two-browser race, or a second submission after setup already completed.
    /// </summary>
    AdministratorAlreadyExists,
}

/// <summary>
/// The result of <see cref="IFirstAdministratorBootstrap.CreateFirstAdministratorAsync"/>. The
/// plaintext recovery codes are populated only when <see cref="Status"/> is
/// <see cref="FirstAdministratorBootstrapStatus.Created"/>, and are shown to the operator exactly
/// once (they are stored only as SHA-256 hashes, §3.4).
/// </summary>
public sealed record FirstAdministratorBootstrapResult(
    FirstAdministratorBootstrapStatus Status,
    IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// Creates the very first administrator (TECHNICAL_SPECIFICATION §3.6). Two operations:
/// <see cref="AdministratorExistsAsync"/> is the cheap, unlocked gate the <c>/setup</c> page and
/// endpoints use to decide whether the wizard is reachable at all; <see cref="CreateFirstAdministratorAsync"/>
/// is the authoritative, serialized commit.
/// </summary>
public interface IFirstAdministratorBootstrap
{
    /// <summary>
    /// True when at least one <c>administrator</c> role grant exists. Used only as the user-facing
    /// gate (render the wizard, or 404); the definitive check happens under the advisory lock inside
    /// <see cref="CreateFirstAdministratorAsync"/>, so a race here is harmless.
    /// </summary>
    Task<bool> AdministratorExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the person, their passkey, their TOTP secret, ten fresh recovery codes, and the
    /// self-granted <c>administrator</c> role — plus the matching <c>security_event</c> rows — in a
    /// single transaction that first takes <c>pg_advisory_xact_lock(hashtext('myrestaurant_setup'))</c>
    /// and re-checks the zero-administrator condition under that lock. If an administrator already
    /// exists, nothing is written and <see cref="FirstAdministratorBootstrapStatus.AdministratorAlreadyExists"/>
    /// is returned (§3.6: "one wins, the other sees 404 on retry").
    /// </summary>
    Task<FirstAdministratorBootstrapResult> CreateFirstAdministratorAsync(
        NewAdministrator administrator,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IFirstAdministratorBootstrap"/>. It owns its own
/// connection and transaction for the commit (the Identity stores each open a connection per method,
/// so they cannot share one transaction — the whole point of §3.6 is that this is atomic), and it
/// protects the TOTP secret with the exact same Data-Protection purpose
/// (<see cref="DapperUserStore.TotpSecretProtectorPurpose"/>) the store unprotects it with, so the
/// new administrator's authenticator works on their first sign-in.
///
/// <para>Every row is stamped with one <see cref="IClock.UtcNow"/> instant, and all surrogate
/// identifiers are minted with the application <see cref="IIdentifierFactory"/> (UUIDv7, ADR-0011),
/// matching how the rest of the data layer writes.</para>
/// </summary>
public sealed class DapperFirstAdministratorBootstrap : IFirstAdministratorBootstrap
{
    /// <summary>
    /// The advisory-lock key text (§3.6). <c>hashtext</c> maps it to the <c>integer</c> the
    /// transaction-scoped lock takes; the lock is released automatically on commit or rollback.
    /// </summary>
    private const string AdvisoryLockKey = "myrestaurant_setup";

    /// <summary>The role granted to the first administrator (matches the <c>person_role</c> CHECK, §3.7).</summary>
    private const string AdministratorRole = "administrator";

    private const string AdministratorExistsSql =
        "SELECT EXISTS (SELECT 1 FROM person_role WHERE role_name = @Role);";

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;
    private readonly IDataProtector _totpSecretProtector;

    public DapperFirstAdministratorBootstrap(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;

        // Same purpose the store protects/unprotects the at-rest secret with, so the new admin's
        // authenticator secret round-trips on their first sign-in.
        _totpSecretProtector = dataProtectionProvider.CreateProtector(DapperUserStore.TotpSecretProtectorPurpose);
    }

    public async Task<bool> AdministratorExistsAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            AdministratorExistsSql,
            new { Role = AdministratorRole },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<FirstAdministratorBootstrapResult> CreateFirstAdministratorAsync(
        NewAdministrator administrator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(administrator.Passkey);
        ArgumentException.ThrowIfNullOrEmpty(administrator.Username);
        ArgumentException.ThrowIfNullOrEmpty(administrator.PasswordHash);
        ArgumentException.ThrowIfNullOrEmpty(administrator.TotpSecretBase32);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // (1) Serialize concurrent setups on a transaction-scoped advisory lock. A second browser
        // that got past the unlocked gate blocks here until this transaction ends.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@Key));",
            new { Key = AdvisoryLockKey },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // (2) Re-check the zero-administrator condition UNDER the lock — the authoritative gate.
        bool administratorExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            AdministratorExistsSql,
            new { Role = AdministratorRole },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (administratorExists)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new FirstAdministratorBootstrapResult(
                FirstAdministratorBootstrapStatus.AdministratorAlreadyExists,
                []);
        }

        // (3) The person row. must_change_password / must_enroll_totp are false: this account
        // enrolled its own TOTP here, so it carries no obligations (§3.5). is_active is true.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person (
                person_identifier, username, display_name, email_address, phone_number,
                password_hash, totp_secret_protected, must_change_password, must_enroll_totp,
                security_stamp, failed_access_count, lockout_end_at, is_active, created_at)
            VALUES (
                @PersonIdentifier, @Username, @DisplayName, NULL, NULL,
                @PasswordHash, @TotpSecretProtected, false, false,
                @SecurityStamp, 0, NULL, true, @CreatedAt);
            """,
            new
            {
                administrator.PersonIdentifier,
                administrator.Username,
                administrator.DisplayName,
                administrator.PasswordHash,
                TotpSecretProtected = _totpSecretProtector.Protect(administrator.TotpSecretBase32),
                SecurityStamp = Guid.NewGuid(),
                CreatedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // (4) The confirmed passkey. Mirrors DapperUserStore's insert: attestation object and
        // client-data JSON are not stored (attestation is 'none', §3.3); the backup-eligible bit and
        // the other WebAuthn flags are (assertion reads them — migration 0002).
        UserPasskeyInfo passkey = administrator.Passkey;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO passkey_credential (
                passkey_credential_identifier, person_identifier, credential_id, public_key,
                signature_counter, transports, credential_display_name, created_at,
                is_user_verified, is_backup_eligible, is_backed_up)
            VALUES (
                @PasskeyCredentialIdentifier, @PersonIdentifier, @CredentialId, @PublicKey,
                @SignatureCounter, @Transports, @CredentialDisplayName, @CreatedAt,
                @IsUserVerified, @IsBackupEligible, @IsBackedUp);
            """,
            new
            {
                PasskeyCredentialIdentifier = _identifierFactory.Create(),
                administrator.PersonIdentifier,
                CredentialId = passkey.CredentialId,
                PublicKey = passkey.PublicKey,
                SignatureCounter = (long)passkey.SignCount,
                Transports = JoinTransports(passkey.Transports),
                CredentialDisplayName = passkey.Name,
                CreatedAt = now,
                passkey.IsUserVerified,
                passkey.IsBackupEligible,
                passkey.IsBackedUp,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // (5) Ten fresh single-use recovery codes, stored SHA-256-hashed (§3.4). The plaintext is
        // returned to the caller to display once and never persisted.
        IReadOnlyList<string> recoveryCodes = RecoveryCode.GenerateSet();
        var recoveryRows = recoveryCodes.Select(code => new
        {
            TotpRecoveryCodeIdentifier = _identifierFactory.Create(),
            administrator.PersonIdentifier,
            CodeHash = Sha256Hashing.Hash(code),
            CreatedAt = now,
        }).ToList();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO totp_recovery_code (totp_recovery_code_identifier, person_identifier, code_hash, created_at)
            VALUES (@TotpRecoveryCodeIdentifier, @PersonIdentifier, @CodeHash, @CreatedAt);
            """,
            recoveryRows,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // (6) The administrator grant, self-referencing its grantor (§3.6): the new administrator is
        // recorded as their own granter, satisfying person_role.granted_by_person_identifier NOT NULL.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person_role (
                person_role_identifier, person_identifier, role_name, granted_by_person_identifier, granted_at)
            VALUES (
                @PersonRoleIdentifier, @PersonIdentifier, @RoleName, @PersonIdentifier, @GrantedAt);
            """,
            new
            {
                PersonRoleIdentifier = _identifierFactory.Create(),
                administrator.PersonIdentifier,
                RoleName = AdministratorRole,
                GrantedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // (7) The audit trail (§3.7). The self-actions carry a NULL actor; the role grant records the
        // new administrator as their own actor, matching the self-grant above.
        var securityEvents = new[]
        {
            NewSecurityEvent(administrator.PersonIdentifier, actor: null, SecurityEventType.AccountCreated, now),
            NewSecurityEvent(administrator.PersonIdentifier, actor: null, SecurityEventType.PasskeyRegistered, now),
            NewSecurityEvent(administrator.PersonIdentifier, actor: null, SecurityEventType.TotpEnrolled, now),
            NewSecurityEvent(administrator.PersonIdentifier, actor: administrator.PersonIdentifier, SecurityEventType.RoleGranted, now),
        };

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO security_event (
                security_event_identifier, subject_person_identifier, actor_person_identifier, event_type, occurred_at)
            VALUES (
                @SecurityEventIdentifier, @SubjectPersonIdentifier, @ActorPersonIdentifier, @EventType, @OccurredAt);
            """,
            securityEvents,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new FirstAdministratorBootstrapResult(
            FirstAdministratorBootstrapStatus.Created,
            recoveryCodes);
    }

    private SecurityEventRow NewSecurityEvent(Guid subject, Guid? actor, string eventType, DateTimeOffset occurredAt)
        => new(_identifierFactory.Create(), subject, actor, eventType, occurredAt);

    // Transports are opaque tokens the server only echoes back; store them comma-joined (tokens never
    // contain commas), matching DapperUserStore. Null when the authenticator reported none.
    private static string? JoinTransports(string[]? transports)
        => transports is { Length: > 0 } ? string.Join(',', transports) : null;

    private sealed record SecurityEventRow(
        Guid SecurityEventIdentifier,
        Guid SubjectPersonIdentifier,
        Guid? ActorPersonIdentifier,
        string EventType,
        DateTimeOffset OccurredAt);
}
