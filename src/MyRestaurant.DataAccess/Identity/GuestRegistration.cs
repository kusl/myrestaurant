using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// A guest account assembled by the <c>/register</c> surface and ready to be written
/// (TECHNICAL_SPECIFICATION §4.3, §11.1 — "guests self-register at the moment of joining a table:
/// username, optional display name, and at least one credential — passkey offered first, password
/// accepted").
///
/// <para><b>At least one credential is a precondition, not a validation result.</b> Either
/// <paramref name="PasswordHash"/> or <paramref name="Passkey"/> must be present, and
/// <see cref="IGuestRegistration.RegisterAsync"/> throws when neither is — a credential-less account
/// could never be signed into and nothing in the product can create one, so it is a caller bug rather
/// than a user error. Both may be present: a guest who set a password and then also registered a
/// passkey gets both, which is the §3.3 shape ("always offered, never required").</para>
/// </summary>
/// <param name="PersonIdentifier">
/// The person's UUIDv7, minted by the registration surface at its first step so it can double as the
/// stable WebAuthn user handle for the passkey ceremony — it must equal the eventual <c>person</c>
/// row's id, because a discoverable-credential sign-in returns it and the framework matches on it
/// (ADR-0011, §3.3).
/// </param>
/// <param name="Username">The unique <c>citext</c> username, 3–64 characters (§3.1).</param>
/// <param name="DisplayName">Optional display name shown on the party roster and the kitchen queue.</param>
/// <param name="PasswordHash">
/// The Argon2id PHC string (§3.2), already hashed by the caller — this service never sees a
/// plaintext — or <c>null</c> for a passkey-only account, which §3.2 explicitly permits
/// (<c>person.password_hash</c> is nullable).
/// </param>
/// <param name="Passkey">
/// The verified attestation result, or <c>null</c> when the guest declined the passkey and kept a
/// password. Only its registration fields are used; the row's <c>created_at</c> is stamped here so
/// every inserted row shares one instant.
/// </param>
public sealed record NewGuestAccount(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    string? PasswordHash,
    UserPasskeyInfo? Passkey);

/// <summary>The outcome of <see cref="IGuestRegistration.RegisterAsync"/>.</summary>
public enum GuestRegistrationStatus
{
    /// <summary>The account, its optional passkey, and its audit rows were written in one transaction.</summary>
    Registered,

    /// <summary>
    /// The username was already taken (the <c>person.username</c> UNIQUE constraint tripped, and
    /// <c>citext</c> makes that case-insensitive); nothing was written.
    /// </summary>
    UsernameTaken,
}

/// <summary>
/// Guest self-registration (TECHNICAL_SPECIFICATION §4.3, §4.4, §11.1). One operation: write a person
/// with no roles, no TOTP, no obligations, and at least one credential — plus the matching
/// append-only <c>security_event</c> rows — in a single transaction.
///
/// <para><b>Why this is not a method on <see cref="IAccountAdministration"/>.</b> Everything there
/// takes a <c>grantedByPersonIdentifier</c> / <c>changedByPersonIdentifier</c> and records that
/// administrator as the actor, because §3.7 is about one person acting on another's account. A guest
/// registering has no actor: the subject is doing it to themselves, so the <c>account_created</c> row
/// carries a NULL actor, exactly as the <c>/setup</c> bootstrap's self-actions do (§3.6). Sharing an
/// interface would mean a required parameter that this path has nothing to put in.</para>
///
/// <para><b>Why no advisory lock.</b> <see cref="IFirstAdministratorBootstrap"/> serializes on
/// <c>pg_advisory_xact_lock</c> because its precondition is global ("zero administrators exist") and
/// cannot be expressed as a constraint. Registration has no such invariant — the only race is two
/// people claiming one username at once, and the UNIQUE constraint decides that correctly and
/// cheaply. The loser gets <see cref="GuestRegistrationStatus.UsernameTaken"/> and nothing is
/// written.</para>
/// </summary>
public interface IGuestRegistration
{
    /// <summary>
    /// Writes the person row, the passkey credential when one was registered, and the
    /// <c>account_created</c> (plus <c>passkey_registered</c>) audit rows in one transaction (§4.3,
    /// §3.7). The account carries <b>no role</b> — a guest is the absence of a role grant (§3.7) — and
    /// neither obligation flag, because it chose its own credentials and so has nothing outstanding
    /// (§3.5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Neither a password hash nor a passkey was supplied, which would create an account nobody could
    /// ever sign into.
    /// </exception>
    Task<GuestRegistrationStatus> RegisterAsync(
        NewGuestAccount account,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IGuestRegistration"/>. Like
/// <see cref="DapperFirstAdministratorBootstrap"/> and <see cref="DapperAccountAdministration"/> it
/// owns its own connection and transaction (the Identity stores open a connection per method, so they
/// cannot share one — and §4.3's account plus its credential plus its audit trail must land together),
/// stamps every row with one <see cref="IClock.UtcNow"/> instant, and mints every surrogate identifier
/// with the application <see cref="IIdentifierFactory"/> (UUIDv7, ADR-0011).
///
/// <para>No data-protection dependency: unlike the bootstrap this never writes a TOTP secret. A guest
/// is not offered TOTP at all (§3.4 pairs it with the password path for staff and administrators);
/// they may enroll voluntarily later from <c>/account/enroll-totp</c>.</para>
/// </summary>
public sealed class DapperGuestRegistration : IGuestRegistration
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperGuestRegistration(
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

    public async Task<GuestRegistrationStatus> RegisterAsync(
        NewGuestAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(account.Username);

        if (string.IsNullOrEmpty(account.PasswordHash) && account.Passkey is null)
        {
            throw new ArgumentException(
                "A guest account needs at least one credential — a password hash, a passkey, or both"
                + " (§4.3). Registering with neither would create an account nobody could sign into.",
                nameof(account));
        }

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // (1) The person row. No role (a guest is the absence of a grant, §3.7), no TOTP, no contact
        // details, neither obligation flag set — this account chose its own credentials, so §3.5 has
        // nothing outstanding against it — active, and a fresh security stamp minted. password_hash is
        // NULL for a passkey-only account, which the schema permits (§8.2) and §3.2 intends.
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
                    @PasswordHash, NULL, false, false,
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
            // Either the name was taken before this request started (the surface's pre-check missed a
            // race) or two guests typed the same name at once. Both read the same to the loser.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return GuestRegistrationStatus.UsernameTaken;
        }

        // (2) The passkey, when there is one. Mirrors DapperUserStore's and the bootstrap's insert:
        // the attestation object and client-data JSON are not stored (attestation is 'none', §3.3);
        // the three WebAuthn flags are, because assertion reads the backup-eligible bit back and fails
        // the ceremony on a mismatch (migration 0002).
        if (account.Passkey is { } passkey)
        {
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
                    account.PersonIdentifier,
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
        }

        // (3) The audit trail (§3.7). Both are self-actions, so the actor is NULL — the same shape the
        // /setup bootstrap writes for the account it creates (§3.6). No role_granted row: there is no
        // role.
        List<SecurityEventRow> events =
        [
            NewSecurityEvent(account.PersonIdentifier, SecurityEventType.AccountCreated, now),
        ];

        if (account.Passkey is not null)
        {
            events.Add(NewSecurityEvent(account.PersonIdentifier, SecurityEventType.PasskeyRegistered, now));
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return GuestRegistrationStatus.Registered;
    }

    private SecurityEventRow NewSecurityEvent(Guid subject, string eventType, DateTimeOffset occurredAt)
        => new(_identifierFactory.Create(), subject, ActorPersonIdentifier: null, eventType, occurredAt);

    // Transports are opaque tokens the server only echoes back; store them comma-joined (tokens never
    // contain commas), matching DapperUserStore and the bootstrap. Null when none were reported.
    private static string? JoinTransports(string[]? transports)
        => transports is { Length: > 0 } ? string.Join(',', transports) : null;

    // Dapper maps this positional record by parameter name against the INSERT's @-parameters; the
    // actor is a Guid? so Npgsql resolves the parameter type even though every row here passes NULL.
    private sealed record SecurityEventRow(
        Guid SecurityEventIdentifier,
        Guid SubjectPersonIdentifier,
        Guid? ActorPersonIdentifier,
        string EventType,
        DateTimeOffset OccurredAt);
}
