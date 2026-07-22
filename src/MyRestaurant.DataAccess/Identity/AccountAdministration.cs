using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// The details for a new staff account created by an administrator (TECHNICAL_SPECIFICATION §3.7). The
/// password is already Argon2id-hashed by the caller (§3.2) — this service never sees the plaintext —
/// and the account is created with <c>must_change_password</c> set, so the temporary password the
/// administrator hands out is replaced through the obligations pipeline (§3.5) on the first sign-in.
/// </summary>
/// <param name="PersonIdentifier">
/// The person's UUIDv7, minted by the caller (ADR-0011) so it is stable if the caller needs to
/// reference the account immediately after creation; it becomes the <c>person</c> row's id.
/// </param>
/// <param name="Username">The unique <c>citext</c> username, 3–64 characters (§3.1).</param>
/// <param name="DisplayName">Optional display name shown on rosters and the kitchen queue.</param>
/// <param name="PasswordHash">The Argon2id PHC string (§3.2); the caller hashed the temporary password already.</param>
/// <param name="Roles">
/// The staff roles to grant at creation — any of <c>administrator</c>, <c>counter</c>, <c>kitchen</c>
/// (§3.7). May be empty (an account with no role behaves like a guest until one is granted).
/// </param>
public sealed record NewStaffAccount(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    string PasswordHash,
    IReadOnlyList<string> Roles);

/// <summary>The outcome of <see cref="IAccountAdministration.CreateStaffAsync"/>.</summary>
public enum CreateStaffStatus
{
    /// <summary>The account and its role grants were written in one transaction.</summary>
    Created,

    /// <summary>The username was already taken (the <c>person.username</c> UNIQUE constraint tripped); nothing was written.</summary>
    UsernameTaken,
}

/// <summary>The outcome of <see cref="IAccountAdministration.GrantRoleAsync"/>.</summary>
public enum RoleGrantOutcome
{
    /// <summary>The role was granted; the subject's security stamp was rotated and a <c>role_granted</c> event recorded.</summary>
    Granted,

    /// <summary>The subject already held the role; nothing changed.</summary>
    AlreadyHeld,

    /// <summary>No person exists with the given identifier.</summary>
    PersonNotFound,
}

/// <summary>The outcome of <see cref="IAccountAdministration.RevokeRoleAsync"/>.</summary>
public enum RoleRevokeOutcome
{
    /// <summary>The role was revoked; the subject's security stamp was rotated and a <c>role_revoked</c> event recorded.</summary>
    Revoked,

    /// <summary>The subject did not hold the role; nothing changed.</summary>
    NotHeld,

    /// <summary>No person exists with the given identifier.</summary>
    PersonNotFound,

    /// <summary>
    /// The subject was the only holder of the <c>administrator</c> role, so the revoke was refused to
    /// keep the system from ending up with no administrator at all (the state <c>/setup</c> opens in);
    /// nothing changed.
    /// </summary>
    WouldRemoveLastAdministrator,
}

/// <summary>The outcome of <see cref="IAccountAdministration.ResetCredentialsAsync"/>.</summary>
public enum CredentialResetOutcome
{
    /// <summary>The credentials were reset in one transaction (see <see cref="CredentialResetResult"/> for detail).</summary>
    Reset,

    /// <summary>No person exists with the given identifier; nothing changed.</summary>
    PersonNotFound,
}

/// <summary>
/// The result of an administrative credential reset (§3.7). <see cref="ClearedAuthenticator"/> is true
/// when the account had TOTP enrolled and it was cleared (secret + recovery codes removed and
/// <c>must_enroll_totp</c> set), which is exactly when a <c>totp_cleared_by_administrator</c> event was
/// also recorded alongside the always-present <c>password_reset_by_administrator</c> event.
/// </summary>
public sealed record CredentialResetResult(CredentialResetOutcome Outcome, bool ClearedAuthenticator);

/// <summary>The outcome of <see cref="IAccountAdministration.SetAccountActiveAsync"/>.</summary>
public enum AccountActivationOutcome
{
    /// <summary>The account's active state changed; the security stamp was rotated and the matching event recorded.</summary>
    Changed,

    /// <summary>The account was already in the requested state; nothing changed.</summary>
    NoChange,

    /// <summary>No person exists with the given identifier; nothing changed.</summary>
    PersonNotFound,

    /// <summary>
    /// Deactivating this account would have left no <em>active</em> administrator, so it was refused to
    /// keep at least one administrator able to sign in; nothing changed.
    /// </summary>
    WouldDeactivateLastAdministrator,
}

/// <summary>
/// Administrative account management (TECHNICAL_SPECIFICATION §3.7): create staff, grant and revoke
/// roles, reset credentials, and deactivate/reactivate accounts. Every mutation that changes a
/// credential or a role also rotates the subject's <c>security_stamp</c> (§3.1) so the change bites the
/// subject's live sessions within the revalidation window, and every mutation records the matching
/// append-only <c>security_event</c> row (§3.7) — with the acting administrator as the actor — in the
/// <em>same</em> transaction as the change.
///
/// <para>This is the transactional companion the read-only <see cref="IPersonDirectory"/> pairs with:
/// grant/revoke need the granting administrator, which the parameterless Identity
/// <c>AddToRoleAsync</c>/<c>RemoveFromRoleAsync</c> store contract cannot supply
/// (<c>person_role.granted_by_person_identifier</c> is NOT NULL), so those — and the multi-table reset
/// and creation flows — live here rather than on <c>UserManager</c>. It reuses the self-contained
/// connection/transaction pattern <see cref="IFirstAdministratorBootstrap"/> established (§3.6).</para>
/// </summary>
public interface IAccountAdministration
{
    /// <summary>
    /// Creates a staff account with <c>must_change_password</c> set and grants the requested roles, all
    /// in one transaction (§3.7). Records <c>account_created</c> plus a <c>role_granted</c> per role,
    /// with the acting administrator as the actor.
    /// </summary>
    Task<CreateStaffStatus> CreateStaffAsync(
        NewStaffAccount account,
        Guid grantedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants <paramref name="roleName"/> to the subject if not already held, rotating the subject's
    /// security stamp and recording <c>role_granted</c> (§3.7). Idempotent: an already-held role is a
    /// no-op (<see cref="RoleGrantOutcome.AlreadyHeld"/>).
    /// </summary>
    Task<RoleGrantOutcome> GrantRoleAsync(
        Guid personIdentifier,
        string roleName,
        Guid grantedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes <paramref name="roleName"/> from the subject if held, rotating the subject's security
    /// stamp and recording <c>role_revoked</c> (§3.7). Refuses to remove the last <c>administrator</c>
    /// (<see cref="RoleRevokeOutcome.WouldRemoveLastAdministrator"/>).
    /// </summary>
    Task<RoleRevokeOutcome> RevokeRoleAsync(
        Guid personIdentifier,
        string roleName,
        Guid revokedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets an account's credentials (§3.7): stores the supplied temporary password hash and sets
    /// <c>must_change_password</c>; if TOTP was enrolled, clears the secret and recovery codes and sets
    /// <c>must_enroll_totp</c>; rotates the security stamp; and records
    /// <c>password_reset_by_administrator</c> (plus <c>totp_cleared_by_administrator</c> when the
    /// authenticator was cleared). All in one transaction.
    /// </summary>
    Task<CredentialResetResult> ResetCredentialsAsync(
        Guid personIdentifier,
        string temporaryPasswordHash,
        Guid resetByPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the account's active state (§3.7, F-10b — accounts are deactivated, never deleted). A change
    /// rotates the security stamp (so a deactivation invalidates live sessions) and records
    /// <c>account_deactivated</c> or <c>account_reactivated</c>. Refuses to deactivate the last active
    /// administrator (<see cref="AccountActivationOutcome.WouldDeactivateLastAdministrator"/>).
    /// </summary>
    Task<AccountActivationOutcome> SetAccountActiveAsync(
        Guid personIdentifier,
        bool isActive,
        Guid changedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IAccountAdministration"/>. Like
/// <see cref="DapperFirstAdministratorBootstrap"/> it owns its own connection and transaction per
/// operation (so the row change and its audit event commit atomically), stamps every row with one
/// <see cref="IClock.UtcNow"/> instant, and mints all surrogate identifiers with the application
/// <see cref="IIdentifierFactory"/> (UUIDv7, ADR-0011). A fresh <see cref="Guid"/> is written to
/// <c>person.security_stamp</c> on every credential/role/active change (§3.1); it holds no TOTP
/// plaintext, so it needs no data-protection dependency (the reset path only clears the secret).
/// </summary>
public sealed class DapperAccountAdministration : IAccountAdministration
{
    /// <summary>The stored role vocabulary (matches the <c>person_role.role_name</c> CHECK, §3.7), lower case.</summary>
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

        // (1) The person row. Created with must_change_password set so the temporary password is
        // replaced through the obligations pipeline on first sign-in (§3.5); no TOTP, no contact
        // details, active, a fresh security stamp minted. A duplicate username is the losing side of a
        // race or a taken name — surface it cleanly rather than as a raw PostgreSQL error.
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

        // (2) The role grants, each recording the acting administrator as grantor (§3.7).
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

        // (3) The audit trail (§3.7): account_created, then one role_granted per role, all actored by
        // the administrator who created the account.
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

        // A role change rotates the stamp so the new claim reaches the subject's sessions (§3.1).
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

        // Never remove the last administrator: that would leave the system with no administrator at all
        // — the very condition /setup reopens in (§3.6) — so refuse it under the lock of this transaction.
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

        // Existence + current TOTP-enrollment state in one read; a null row means no such person.
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

        // Always: temporary password, forced change, fresh stamp (§3.7, §3.1).
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

        // Conditionally: clear an enrolled authenticator and force re-enrollment (§3.7). Enrollment is
        // derived from totp_secret_protected (§3.4), so nulling it disables two-factor; the recovery
        // codes are removed with it.
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

        // The audit trail (§3.7): the password reset always, the TOTP clearing only when it happened.
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

        // Never deactivate the last active administrator: at least one administrator must remain able to
        // sign in (§3.7). The subject is currently active here (we are deactivating it), so if it is an
        // administrator and it is the only active one, refuse.
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

        // A deactivation must invalidate live sessions, so rotate the stamp on the state change (§3.1, §3.7).
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

    // --- shared SQL helpers (all scoped to the caller's transaction) --------------------------------

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

        // De-duplicate (a repeated role would trip the person_role UNIQUE constraint) while validating.
        LinkedHashSet ordered = new();
        foreach (string role in roles)
        {
            ordered.Add(NormalizeRole(role));
        }

        return ordered.ToList();
    }

    // Dapper maps this positional record by parameter name against the INSERT's @-parameters; the
    // nullable actor is a Guid? so Npgsql resolves the parameter type for both a value and NULL
    // (mirroring DapperFirstAdministratorBootstrap's batch insert).
    private sealed record SecurityEventRow(
        Guid SecurityEventIdentifier,
        Guid SubjectPersonIdentifier,
        Guid? ActorPersonIdentifier,
        string EventType,
        DateTimeOffset OccurredAt);

    private sealed record ActivationRow(bool IsActive, bool IsAdministrator);

    private sealed record ResetProbeRow(bool HasAuthenticator);

    // A tiny insertion-order-preserving set so the granted roles keep the order the caller listed them
    // (the audit events then read naturally) without pulling in a dependency.
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
