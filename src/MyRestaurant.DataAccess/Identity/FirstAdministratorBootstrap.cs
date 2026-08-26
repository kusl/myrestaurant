using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Identity;

public sealed record NewAdministrator(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    string PasswordHash,
    string TotpSecretBase32,
    UserPasskeyInfo Passkey);

public enum FirstAdministratorBootstrapStatus
{
    Created,
    AdministratorAlreadyExists,
}

public sealed record FirstAdministratorBootstrapResult(
    FirstAdministratorBootstrapStatus Status,
    IReadOnlyList<string> RecoveryCodes);

public interface IFirstAdministratorBootstrap
{
    Task<bool> AdministratorExistsAsync(CancellationToken cancellationToken = default);

    Task<FirstAdministratorBootstrapResult> CreateFirstAdministratorAsync(
        NewAdministrator administrator,
        CancellationToken cancellationToken = default);
}

public sealed class DapperFirstAdministratorBootstrap : IFirstAdministratorBootstrap
{
    private const string AdvisoryLockKey = "myrestaurant_setup";

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

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@Key));",
            new { Key = AdvisoryLockKey },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

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

    private static string? JoinTransports(string[]? transports)
        => transports is { Length: > 0 } ? string.Join(',', transports) : null;

    private sealed record SecurityEventRow(
        Guid SecurityEventIdentifier,
        Guid SubjectPersonIdentifier,
        Guid? ActorPersonIdentifier,
        string EventType,
        DateTimeOffset OccurredAt);
}
