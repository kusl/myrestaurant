using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Identity;

public sealed record NewStaffAccount(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    string PasswordHash,
    IReadOnlyList<string> Roles);

public enum CreateStaffStatus
{
    Created,
    UsernameTaken,
}

public enum RoleGrantOutcome
{
    Granted,
    AlreadyHeld,
    PersonNotFound,
}

public enum RoleRevokeOutcome
{
    Revoked,
    NotHeld,
    PersonNotFound,
    WouldRemoveLastAdministrator,
}

public enum CredentialResetOutcome
{
    Reset,
    PersonNotFound,
}

public sealed record CredentialResetResult(CredentialResetOutcome Outcome, bool ClearedAuthenticator);

public enum AccountActivationOutcome
{
    Changed,
    NoChange,
    PersonNotFound,
    WouldDeactivateLastAdministrator,
}

public interface IAccountAdministration
{
    Task<CreateStaffStatus> CreateStaffAsync(
        NewStaffAccount account,
        Guid grantedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RoleGrantOutcome> GrantRoleAsync(
        Guid personIdentifier,
        string roleName,
        Guid grantedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RoleRevokeOutcome> RevokeRoleAsync(
        Guid personIdentifier,
        string roleName,
        Guid revokedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<CredentialResetResult> ResetCredentialsAsync(
        Guid personIdentifier,
        string temporaryPasswordHash,
        Guid resetByPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<AccountActivationOutcome> SetAccountActiveAsync(
        Guid personIdentifier,
        bool isActive,
        Guid changedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperAccountAdministration : IAccountAdministration
{
    private const string AdministratorRole = "administrator";

    private static readonly IReadOnlySet<string> GrantableRoles =
        new HashSet<string>(StringComparer.Ordinal) { AdministratorRole, "counter", "kitchen" };

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperAccountAdministration(
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

    public async Task<CreateStaffStatus> CreateStaffAsync(
        NewStaffAccount account,
        Guid grantedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(account.Username);
        ArgumentException.ThrowIfNullOrEmpty(account.PasswordHash);

        IReadOnlyList<string> roles = NormalizeRoles(account.Roles);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO person (
                    person_identifier, username, display_name, email_address, phone_number,
                    password_hash, totp_secret_protected, must_change_password, must_enroll_totp,
                    security_stamp, failed_access_count, lockout_end_at, is_active, created_at)
                VALUES (
                    @PersonIdentifier, @Username, @DisplayName, NULL, NULL,
                    @PasswordHash, NULL, true, false,
                    @SecurityStamp, 0, NULL, true, @CreatedAt);
                """,
                new
                {
                    account.PersonIdentifier,
                    account.Username,
                    account.DisplayName,
                    account.PasswordHash,
                    SecurityStamp = Guid.NewGuid(),
                    CreatedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return CreateStaffStatus.UsernameTaken;
        }

        foreach (string role in roles)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO person_role (
                    person_role_identifier, person_identifier, role_name, granted_by_person_identifier, granted_at)
                VALUES (
                    @PersonRoleIdentifier, @PersonIdentifier, @RoleName, @GrantedBy, @GrantedAt);
                """,
                new
                {
                    PersonRoleIdentifier = _identifierFactory.Create(),
                    account.PersonIdentifier,
                    RoleName = role,
                    GrantedBy = grantedByPersonIdentifier,
                    GrantedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        List<SecurityEventRow> events =
        [
            NewSecurityEvent(account.PersonIdentifier, grantedByPersonIdentifier, SecurityEventType.AccountCreated, now),
        ];
        events.AddRange(roles.Select(_ =>
            NewSecurityEvent(account.PersonIdentifier, grantedByPersonIdentifier, SecurityEventType.RoleGranted, now)));

        await InsertSecurityEventsAsync(connection, transaction, events, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CreateStaffStatus.Created;
    }

    public async Task<RoleGrantOutcome> GrantRoleAsync(
        Guid personIdentifier,
        string roleName,
        Guid grantedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string role = NormalizeRole(roleName);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await PersonExistsAsync(connection, transaction, personIdentifier, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RoleGrantOutcome.PersonNotFound;
        }

        if (await HoldsRoleAsync(connection, transaction, personIdentifier, role, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RoleGrantOutcome.AlreadyHeld;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person_role (
                person_role_identifier, person_identifier, role_name, granted_by_person_identifier, granted_at)
            VALUES (
                @PersonRoleIdentifier, @PersonIdentifier, @RoleName, @GrantedBy, @GrantedAt);
            """,
            new
            {
                PersonRoleIdentifier = _identifierFactory.Create(),
                PersonIdentifier = personIdentifier,
                RoleName = role,
                GrantedBy = grantedByPersonIdentifier,
                GrantedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await RotateSecurityStampAsync(connection, transaction, personIdentifier, cancellationToken).ConfigureAwait(false);

        await InsertSecurityEventsAsync(
            connection,
            transaction,
            [NewSecurityEvent(personIdentifier, grantedByPersonIdentifier, SecurityEventType.RoleGranted, now)],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RoleGrantOutcome.Granted;
    }

    public async Task<RoleRevokeOutcome> RevokeRoleAsync(
        Guid personIdentifier,
        string roleName,
        Guid revokedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string role = NormalizeRole(roleName);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await PersonExistsAsync(connection, transaction, personIdentifier, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RoleRevokeOutcome.PersonNotFound;
        }

        if (!await HoldsRoleAsync(connection, transaction, personIdentifier, role, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RoleRevokeOutcome.NotHeld;
        }

        if (role == AdministratorRole)
        {
            int administrators = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM person_role WHERE role_name = @Role;",
                new { Role = AdministratorRole },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (administrators <= 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return RoleRevokeOutcome.WouldRemoveLastAdministrator;
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM person_role WHERE person_identifier = @PersonIdentifier AND role_name = @RoleName;",
            new { PersonIdentifier = personIdentifier, RoleName = role },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await RotateSecurityStampAsync(connection, transaction, personIdentifier, cancellationToken).ConfigureAwait(false);

        await InsertSecurityEventsAsync(
            connection,
            transaction,
            [NewSecurityEvent(personIdentifier, revokedByPersonIdentifier, SecurityEventType.RoleRevoked, now)],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RoleRevokeOutcome.Revoked;
    }

    public async Task<CredentialResetResult> ResetCredentialsAsync(
        Guid personIdentifier,
        string temporaryPasswordHash,
        Guid resetByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporaryPasswordHash);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        ResetProbeRow? probe = await connection.QuerySingleOrDefaultAsync<ResetProbeRow>(new CommandDefinition(
            "SELECT (totp_secret_protected IS NOT NULL) AS HasAuthenticator FROM person WHERE person_identifier = @Id;",
            new { Id = personIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (probe is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CredentialResetResult(CredentialResetOutcome.PersonNotFound, ClearedAuthenticator: false);
        }

        bool clearedAuthenticator = probe.HasAuthenticator;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE person SET
                password_hash        = @PasswordHash,
                must_change_password = true,
                security_stamp       = @SecurityStamp
            WHERE person_identifier  = @Id;
            """,
            new
            {
                PasswordHash = temporaryPasswordHash,
                SecurityStamp = Guid.NewGuid(),
                Id = personIdentifier,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (clearedAuthenticator)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE person SET
                    totp_secret_protected = NULL,
                    must_enroll_totp      = true
                WHERE person_identifier   = @Id;
                """,
                new { Id = personIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM totp_recovery_code WHERE person_identifier = @Id;",
                new { Id = personIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        List<SecurityEventRow> events =
        [
            NewSecurityEvent(personIdentifier, resetByPersonIdentifier, SecurityEventType.PasswordResetByAdministrator, now),
        ];
        if (clearedAuthenticator)
        {
            events.Add(NewSecurityEvent(personIdentifier, resetByPersonIdentifier, SecurityEventType.TotpClearedByAdministrator, now));
        }

        await InsertSecurityEventsAsync(connection, transaction, events, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CredentialResetResult(CredentialResetOutcome.Reset, clearedAuthenticator);
    }

    public async Task<AccountActivationOutcome> SetAccountActiveAsync(
        Guid personIdentifier,
        bool isActive,
        Guid changedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        ActivationRow? current = await connection.QuerySingleOrDefaultAsync<ActivationRow>(new CommandDefinition(
            """
            SELECT
                p.is_active AS IsActive,
                EXISTS (
                    SELECT 1 FROM person_role r
                    WHERE r.person_identifier = p.person_identifier AND r.role_name = @Role
                ) AS IsAdministrator
            FROM person p
            WHERE p.person_identifier = @Id;
            """,
            new { Id = personIdentifier, Role = AdministratorRole },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AccountActivationOutcome.PersonNotFound;
        }

        if (current.IsActive == isActive)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AccountActivationOutcome.NoChange;
        }

        if (!isActive && current.IsAdministrator)
        {
            int activeAdministrators = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT count(*)::int
                FROM person p
                JOIN person_role r ON r.person_identifier = p.person_identifier
                WHERE r.role_name = @Role AND p.is_active = true;
                """,
                new { Role = AdministratorRole },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (activeAdministrators <= 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AccountActivationOutcome.WouldDeactivateLastAdministrator;
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE person SET
                is_active      = @IsActive,
                security_stamp = @SecurityStamp
            WHERE person_identifier = @Id;
            """,
            new
            {
                IsActive = isActive,
                SecurityStamp = Guid.NewGuid(),
                Id = personIdentifier,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        string eventType = isActive ? SecurityEventType.AccountReactivated : SecurityEventType.AccountDeactivated;
        await InsertSecurityEventsAsync(
            connection,
            transaction,
            [NewSecurityEvent(personIdentifier, changedByPersonIdentifier, eventType, now)],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AccountActivationOutcome.Changed;
    }

    private static async Task<bool> PersonExistsAsync(
        DbConnection connection, DbTransaction transaction, Guid personIdentifier, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM person WHERE person_identifier = @Id);",
            new { Id = personIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task<bool> HoldsRoleAsync(
        DbConnection connection, DbTransaction transaction, Guid personIdentifier, string roleName, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM person_role WHERE person_identifier = @Id AND role_name = @Role);",
            new { Id = personIdentifier, Role = roleName },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task RotateSecurityStampAsync(
        DbConnection connection, DbTransaction transaction, Guid personIdentifier, CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE person SET security_stamp = @SecurityStamp WHERE person_identifier = @Id;",
            new { SecurityStamp = Guid.NewGuid(), Id = personIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task InsertSecurityEventsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<SecurityEventRow> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO security_event (
                security_event_identifier, subject_person_identifier, actor_person_identifier, event_type, occurred_at)
            VALUES (
                @SecurityEventIdentifier, @SubjectPersonIdentifier, @ActorPersonIdentifier, @EventType, @OccurredAt);
            """,
            events,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private SecurityEventRow NewSecurityEvent(Guid subject, Guid? actor, string eventType, DateTimeOffset occurredAt)
        => new(_identifierFactory.Create(), subject, actor, eventType, occurredAt);

    private static string NormalizeRole(string roleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(roleName);
        string role = roleName.Trim().ToLowerInvariant();
        if (!GrantableRoles.Contains(role))
        {
            throw new ArgumentOutOfRangeException(
                nameof(roleName),
                roleName,
                "Role must be one of 'administrator', 'counter', or 'kitchen' (§3.7).");
        }

        return role;
    }

    private static IReadOnlyList<string> NormalizeRoles(IReadOnlyList<string>? roles)
    {
        if (roles is null || roles.Count == 0)
        {
            return [];
        }

        LinkedHashSet ordered = new();
        foreach (string role in roles)
        {
            ordered.Add(NormalizeRole(role));
        }

        return ordered.ToList();
    }

    private sealed record SecurityEventRow(
        Guid SecurityEventIdentifier,
        Guid SubjectPersonIdentifier,
        Guid? ActorPersonIdentifier,
        string EventType,
        DateTimeOffset OccurredAt);

    private sealed record ActivationRow(bool IsActive, bool IsAdministrator);

    private sealed record ResetProbeRow(bool HasAuthenticator);

    private sealed class LinkedHashSet
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        public void Add(string value)
        {
            if (_seen.Add(value))
            {
                _order.Add(value);
            }
        }

        public List<string> ToList() => [.. _order];
    }
}
