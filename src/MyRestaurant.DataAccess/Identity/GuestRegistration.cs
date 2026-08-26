using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Identity;

public sealed record NewGuestAccount(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    string? PasswordHash,
    UserPasskeyInfo? Passkey);

public enum GuestRegistrationStatus
{
    Registered,
    UsernameTaken,
}

public interface IGuestRegistration
{
    Task<GuestRegistrationStatus> RegisterAsync(
        NewGuestAccount account,
        CancellationToken cancellationToken = default);
}

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
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return GuestRegistrationStatus.UsernameTaken;
        }

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

    private static string? JoinTransports(string[]? transports)
        => transports is { Length: > 0 } ? string.Join(',', transports) : null;

    private sealed record SecurityEventRow(
        Guid SecurityEventIdentifier,
        Guid SubjectPersonIdentifier,
        Guid? ActorPersonIdentifier,
        string EventType,
        DateTimeOffset OccurredAt);
}
