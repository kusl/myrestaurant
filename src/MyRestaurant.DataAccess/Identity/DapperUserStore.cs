using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// The custom ASP.NET Core Identity store over the <c>person*</c> tables with Dapper
/// (TECHNICAL_SPECIFICATION §3.1, ADR-0003 — Identity core services over Dapper, never EF). One
/// class implements the whole family a <see cref="UserManager{TUser}"/> needs for passwords,
/// security stamps, lockout, TOTP, recovery codes, roles, email, and phone; the passkey store
/// (<c>IUserPasskeyStore</c>, new in .NET 10) arrives in a dedicated increment. <see cref="UserManager{TUser}"/>
/// discovers each capability by casting the resolved <see cref="IUserStore{TUser}"/> to the
/// relevant interface, so registering this once via <c>AddUserStore&lt;DapperUserStore&gt;()</c> is enough.
///
/// <para>Design notes that matter for correctness:</para>
/// <list type="bullet">
///   <item><b>citext</b> makes <c>username</c>/<c>email_address</c> case-insensitively unique and
///   searchable, so there are no normalized shadow columns; the normalized-* store methods are
///   no-ops and lookups compare against the natural column (cast to <c>citext</c> so the operator
///   resolves regardless of how the driver types the parameter).</item>
///   <item><b>Security stamp</b> is a <c>uuid</c>: <see cref="SetSecurityStampAsync"/> mints a fresh
///   <see cref="Guid"/> and ignores the opaque string Identity passes (see <see cref="Person"/>).</item>
///   <item><b>Two-factor enabled</b> is derived from <c>totp_secret_protected</c>; the TOTP secret is
///   stored Data-Protection-encrypted (§3.4).</item>
///   <item><b>Recovery codes</b> live in their own table, stored SHA-256-hashed, and are single-use;
///   those methods write to the database directly rather than mutating the entity.</item>
///   <item><b>Role grants/revokes</b> are <em>not</em> exposed here: <c>person_role</c> requires the
///   granting administrator (self-referencing for the first admin, §3.6), which the parameterless
///   <c>AddToRoleAsync</c>/<c>RemoveFromRoleAsync</c> contract cannot supply — those run through the
///   transactional account-administration service (a later M2 increment). The read side is fully
///   implemented so role claims flow into the cookie at sign-in.</item>
///   <item><b>Deletion does not exist</b> (F-10b): <see cref="DeleteAsync"/> throws. Accounts are
///   deactivated (<c>is_active=false</c>), never removed, so history keeps its actors.</item>
/// </list>
///
/// The store holds no connection: every method opens one from the injected factory and disposes it,
/// so a single instance is safe for the scoped Identity lifetime.
/// </summary>
public sealed class DapperUserStore :
    IUserStore<Person>,
    IUserPasswordStore<Person>,
    IUserSecurityStampStore<Person>,
    IUserLockoutStore<Person>,
    IUserTwoFactorStore<Person>,
    IUserAuthenticatorKeyStore<Person>,
    IUserTwoFactorRecoveryCodeStore<Person>,
    IUserRoleStore<Person>,
    IUserEmailStore<Person>,
    IUserPhoneNumberStore<Person>
{
    /// <summary>Data-Protection purpose for the at-rest TOTP secret (§3.4). Do not change without a migration plan.</summary>
    private const string TotpSecretProtectorPurpose = "MyRestaurant.Identity.TotpSecret.v1";

    // Every SELECT aliases snake_case columns to the POCO's PascalCase properties. Postgres folds the
    // unquoted aliases to lower case and Dapper matches case-insensitively, so no global
    // MatchNamesWithUnderscores setting is needed (which would silently affect every other query).
    private const string PersonColumns = """
        person_identifier      AS PersonIdentifier,
        username               AS Username,
        display_name           AS DisplayName,
        email_address          AS EmailAddress,
        phone_number           AS PhoneNumber,
        password_hash          AS PasswordHash,
        totp_secret_protected  AS TotpSecretProtected,
        must_change_password   AS MustChangePassword,
        must_enroll_totp       AS MustEnrollTotp,
        security_stamp         AS SecurityStamp,
        failed_access_count    AS FailedAccessCount,
        lockout_end_at         AS LockoutEndAt,
        is_active              AS IsActive,
        created_at             AS CreatedAt
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;
    private readonly IdentityErrorDescriber _errorDescriber;
    private readonly IDataProtector _totpSecretProtector;

    public DapperUserStore(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory,
        IDataProtectionProvider dataProtectionProvider,
        IdentityErrorDescriber errorDescriber)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(errorDescriber);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
        _errorDescriber = errorDescriber;
        _totpSecretProtector = dataProtectionProvider.CreateProtector(TotpSecretProtectorPurpose);
    }

    // ---------------------------------------------------------------------------------------------
    // IUserStore — identity, lookup, and the row's lifecycle (create/update; never delete).
    // ---------------------------------------------------------------------------------------------

    public Task<string> GetUserIdAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.PersonIdentifier.ToString());
    }

    public Task<string?> GetUserNameAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.Username);
    }

    public Task SetUserNameAsync(Person user, string? userName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.Username = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    // No normalized-username column: citext handles case-insensitive uniqueness/lookup (§3.1).
    public Task<string?> GetNormalizedUserNameAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.Username);
    }

    public Task SetNormalizedUserNameAsync(Person user, string? normalizedName, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task<IdentityResult> CreateAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Make direct store usage safe (tests, bootstrap) — UserManager also sets these, harmlessly.
        if (user.PersonIdentifier == Guid.Empty)
        {
            user.PersonIdentifier = _identifierFactory.Create();
        }

        if (user.SecurityStamp == Guid.Empty)
        {
            user.SecurityStamp = Guid.NewGuid();
        }

        if (user.CreatedAt == default)
        {
            user.CreatedAt = _clock.UtcNow;
        }

        const string sql = """
            INSERT INTO person (
                person_identifier, username, display_name, email_address, phone_number,
                password_hash, totp_secret_protected, must_change_password, must_enroll_totp,
                security_stamp, failed_access_count, lockout_end_at, is_active, created_at)
            VALUES (
                @PersonIdentifier, @Username, @DisplayName, @EmailAddress, @PhoneNumber,
                @PasswordHash, @TotpSecretProtected, @MustChangePassword, @MustEnrollTotp,
                @SecurityStamp, @FailedAccessCount, @LockoutEndAt, @IsActive, @CreatedAt);
            """;

        try
        {
            await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, ToParameters(user), cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return IdentityResult.Success;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return IdentityResult.Failed(_errorDescriber.DuplicateUserName(user.Username));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // The only CHECK on person is the 3–64 char username length (§3.1).
            return IdentityResult.Failed(_errorDescriber.InvalidUserName(user.Username));
        }
    }

    public async Task<IdentityResult> UpdateAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        const string sql = """
            UPDATE person SET
                username              = @Username,
                display_name          = @DisplayName,
                email_address         = @EmailAddress,
                phone_number          = @PhoneNumber,
                password_hash         = @PasswordHash,
                totp_secret_protected = @TotpSecretProtected,
                must_change_password  = @MustChangePassword,
                must_enroll_totp      = @MustEnrollTotp,
                security_stamp        = @SecurityStamp,
                failed_access_count   = @FailedAccessCount,
                lockout_end_at        = @LockoutEndAt,
                is_active             = @IsActive
            WHERE person_identifier   = @PersonIdentifier;
            """;

        try
        {
            await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            int affected = await connection
                .ExecuteAsync(new CommandDefinition(sql, ToParameters(user), cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            return affected == 1
                ? IdentityResult.Success
                : IdentityResult.Failed(_errorDescriber.ConcurrencyFailure());
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return IdentityResult.Failed(_errorDescriber.DuplicateUserName(user.Username));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return IdentityResult.Failed(_errorDescriber.InvalidUserName(user.Username));
        }
    }

    /// <summary>Deletion does not exist (F-10b): accounts are deactivated so history keeps its actors.</summary>
    public Task<IdentityResult> DeleteAsync(Person user, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Persons are never deleted (F-10b). Deactivate the account (set is_active=false) instead so "
            + "security and order history retain their actor.");

    public async Task<Person?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        if (!Guid.TryParse(userId, out Guid identifier))
        {
            return null;
        }

        string sql = $"SELECT {PersonColumns} FROM person WHERE person_identifier = @PersonIdentifier;";
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection
            .QuerySingleOrDefaultAsync<Person>(
                new CommandDefinition(sql, new { PersonIdentifier = identifier }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<Person?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedUserName);

        // citext '=' is case-insensitive; cast the parameter so the operator resolves whether the
        // driver sends it as text or unknown.
        string sql = $"SELECT {PersonColumns} FROM person WHERE username = @Username::citext;";
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection
            .QuerySingleOrDefaultAsync<Person>(
                new CommandDefinition(sql, new { Username = normalizedUserName }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // IUserPasswordStore — hash is set/read on the entity; UpdateAsync persists it.
    // ---------------------------------------------------------------------------------------------

    public Task SetPasswordHashAsync(Person user, string? passwordHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.PasswordHash);
    }

    public Task<bool> HasPasswordAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    // ---------------------------------------------------------------------------------------------
    // IUserSecurityStampStore — regenerate a uuid on every set (see the type remarks on Person).
    // ---------------------------------------------------------------------------------------------

    public Task SetSecurityStampAsync(Person user, string stamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        // Identity's opaque Base32 'stamp' does not fit a uuid column; mint a fresh guid instead.
        // Full randomness (v4) is preferable for an unpredictability stamp than a time-ordered v7.
        user.SecurityStamp = Guid.NewGuid();
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.SecurityStamp.ToString());
    }

    // ---------------------------------------------------------------------------------------------
    // IUserLockoutStore — counters/end-date live on the entity; UpdateAsync persists them. Lockout
    // is always enabled (§3.1): 5 consecutive failures lock for 5 minutes.
    // ---------------------------------------------------------------------------------------------

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.LockoutEndAt);
    }

    public Task SetLockoutEndDateAsync(Person user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.LockoutEndAt = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.FailedAccessCount += 1;
        return Task.FromResult(user.FailedAccessCount);
    }

    public Task ResetAccessFailedCountAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.FailedAccessCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.FailedAccessCount);
    }

    public Task<bool> GetLockoutEnabledAsync(Person user, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(Person user, bool enabled, CancellationToken cancellationToken)
        => Task.CompletedTask; // Always enabled; the flag is not stored.

    // ---------------------------------------------------------------------------------------------
    // IUserTwoFactorStore — enabled is derived from the encrypted TOTP secret (§3.4).
    // ---------------------------------------------------------------------------------------------

    public Task<bool> GetTwoFactorEnabledAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(!string.IsNullOrEmpty(user.TotpSecretProtected));
    }

    public Task SetTwoFactorEnabledAsync(Person user, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Enabling is implied by having a confirmed authenticator secret, so setting true is a no-op;
        // disabling clears the secret (and any forced-enrollment obligation), matching "not enrolled".
        if (!enabled)
        {
            user.TotpSecretProtected = null;
            user.MustEnrollTotp = false;
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------
    // IUserAuthenticatorKeyStore — the TOTP secret, stored Data-Protection-encrypted (§3.4).
    // ---------------------------------------------------------------------------------------------

    public Task SetAuthenticatorKeyAsync(Person user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(key);
        user.TotpSecretProtected = _totpSecretProtector.Protect(key);
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? key = string.IsNullOrEmpty(user.TotpSecretProtected)
            ? null
            : _totpSecretProtector.Unprotect(user.TotpSecretProtected);
        return Task.FromResult(key);
    }

    // ---------------------------------------------------------------------------------------------
    // IUserTwoFactorRecoveryCodeStore — own table, SHA-256-hashed, single-use. These write directly.
    // ---------------------------------------------------------------------------------------------

    public async Task ReplaceCodesAsync(Person user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        DateTimeOffset now = _clock.UtcNow;
        var rows = recoveryCodes.Select(code => new
        {
            TotpRecoveryCodeIdentifier = _identifierFactory.Create(),
            PersonIdentifier = user.PersonIdentifier,
            CodeHash = Sha256Hashing.Hash(code),
            CreatedAt = now,
        }).ToList();

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM totp_recovery_code WHERE person_identifier = @PersonIdentifier;",
            new { user.PersonIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (rows.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO totp_recovery_code (totp_recovery_code_identifier, person_identifier, code_hash, created_at)
                VALUES (@TotpRecoveryCodeIdentifier, @PersonIdentifier, @CodeHash, @CreatedAt);
                """,
                rows,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RedeemCodeAsync(Person user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);

        // Match by SHA-256 hash and mark exactly one unused row used. The subselect + single-row
        // update guarantees single-use even in the (astronomically unlikely) event of a hash clash.
        const string sql = """
            UPDATE totp_recovery_code
            SET used_at = @Now
            WHERE totp_recovery_code_identifier = (
                SELECT totp_recovery_code_identifier
                FROM totp_recovery_code
                WHERE person_identifier = @PersonIdentifier
                  AND code_hash = @CodeHash
                  AND used_at IS NULL
                ORDER BY created_at
                LIMIT 1);
            """;

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Now = _clock.UtcNow, user.PersonIdentifier, CodeHash = Sha256Hashing.Hash(code) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<int> CountCodesAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM totp_recovery_code WHERE person_identifier = @PersonIdentifier AND used_at IS NULL;",
            new { user.PersonIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // IUserRoleStore — read side only; grants/revokes need the granting administrator (§3.6) and run
    // through the transactional account-administration service (a later increment).
    // ---------------------------------------------------------------------------------------------

    public Task AddToRoleAsync(Person user, string roleName, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Role grants record the granting administrator (person_role.granted_by_person_identifier is "
            + "NOT NULL; the first admin self-grants, §3.6). Grant via the account-administration service, "
            + "not UserManager.AddToRoleAsync.");

    public Task RemoveFromRoleAsync(Person user, string roleName, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Role revocation is an audited administrative action (security_event 'role_revoked'). Revoke via "
            + "the account-administration service, not UserManager.RemoveFromRoleAsync.");

    public async Task<IList<string>> GetRolesAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> roles = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT role_name FROM person_role WHERE person_identifier = @PersonIdentifier ORDER BY role_name;",
            new { user.PersonIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return roles.ToList();
    }

    public async Task<bool> IsInRoleAsync(Person user, string roleName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(roleName);

        // Stored role names are lower case (CHECK-constrained); Identity hands us the normalized
        // (upper) form, so compare lower-cased.
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM person_role WHERE person_identifier = @PersonIdentifier AND role_name = lower(@RoleName));",
            new { user.PersonIdentifier, RoleName = roleName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IList<Person>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roleName);

        string sql = $"""
            SELECT {PersonColumns}
            FROM person
            JOIN person_role ON person_role.person_identifier = person.person_identifier
            WHERE person_role.role_name = lower(@RoleName)
            ORDER BY person.username;
            """;

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<Person> people = await connection.QueryAsync<Person>(new CommandDefinition(
            sql, new { RoleName = roleName }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return people.ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // IUserEmailStore / IUserPhoneNumberStore — optional contact fields for manual escalation only
    // (§11.1). There is no confirmation concept in the schema, so the confirmed-* accessors are inert.
    // ---------------------------------------------------------------------------------------------

    public Task SetEmailAsync(Person user, string? email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.EmailAddress = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.EmailAddress);
    }

    public Task<bool> GetEmailConfirmedAsync(Person user, CancellationToken cancellationToken)
        => Task.FromResult(true); // Not modeled; sign-in never gates on it (RequireConfirmedEmail=false).

    public Task SetEmailConfirmedAsync(Person user, bool confirmed, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task<Person?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        string sql = $"SELECT {PersonColumns} FROM person WHERE email_address = @Email::citext;";
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Person>(new CommandDefinition(
            sql, new { Email = normalizedEmail }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public Task<string?> GetNormalizedEmailAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.EmailAddress); // citext normalizes at the database.
    }

    public Task SetNormalizedEmailAsync(Person user, string? normalizedEmail, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task SetPhoneNumberAsync(Person user, string? phoneNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.PhoneNumber = phoneNumber;
        return Task.CompletedTask;
    }

    public Task<string?> GetPhoneNumberAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.PhoneNumber);
    }

    public Task<bool> GetPhoneNumberConfirmedAsync(Person user, CancellationToken cancellationToken)
        => Task.FromResult(true); // Not modeled.

    public Task SetPhoneNumberConfirmedAsync(Person user, bool confirmed, CancellationToken cancellationToken)
        => Task.CompletedTask;

    // ---------------------------------------------------------------------------------------------

    public void Dispose()
    {
        // Nothing to release: connections are opened and disposed per method, never held.
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
        => await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    private static object ToParameters(Person user) => new
    {
        user.PersonIdentifier,
        user.Username,
        user.DisplayName,
        user.EmailAddress,
        user.PhoneNumber,
        user.PasswordHash,
        user.TotpSecretProtected,
        user.MustChangePassword,
        user.MustEnrollTotp,
        user.SecurityStamp,
        user.FailedAccessCount,
        user.LockoutEndAt,
        user.IsActive,
        user.CreatedAt,
    };
}
