using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Identity;

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
    IUserPhoneNumberStore<Person>,
    IUserPasskeyStore<Person>
{
    internal const string TotpSecretProtectorPurpose = "MyRestaurant.Identity.TotpSecret.v1";

    private const string PersonColumns = """
        person.person_identifier      AS PersonIdentifier,
        person.username               AS Username,
        person.display_name           AS DisplayName,
        person.email_address          AS EmailAddress,
        person.phone_number           AS PhoneNumber,
        person.password_hash          AS PasswordHash,
        person.totp_secret_protected  AS TotpSecretProtected,
        person.must_change_password   AS MustChangePassword,
        person.must_enroll_totp       AS MustEnrollTotp,
        person.security_stamp         AS SecurityStamp,
        person.failed_access_count    AS FailedAccessCount,
        person.lockout_end_at         AS LockoutEndAt,
        person.is_active              AS IsActive,
        person.created_at             AS CreatedAt
        """;

    private const string PasskeyColumns = """
        credential_id            AS CredentialId,
        public_key               AS PublicKey,
        signature_counter        AS SignatureCounter,
        transports               AS Transports,
        credential_display_name  AS CredentialDisplayName,
        created_at               AS CreatedAt,
        is_user_verified         AS IsUserVerified,
        is_backup_eligible       AS IsBackupEligible,
        is_backed_up             AS IsBackedUp
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

        string sql = $"SELECT {PersonColumns} FROM person WHERE username = @Username::citext;";
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection
            .QuerySingleOrDefaultAsync<Person>(
                new CommandDefinition(sql, new { Username = normalizedUserName }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

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

    public Task SetSecurityStampAsync(Person user, string stamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.SecurityStamp = Guid.NewGuid();
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.SecurityStamp.ToString());
    }

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
        => Task.CompletedTask;

    public Task<bool> GetTwoFactorEnabledAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(!string.IsNullOrEmpty(user.TotpSecretProtected));
    }

    public Task SetTwoFactorEnabledAsync(Person user, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!enabled)
        {
            user.TotpSecretProtected = null;
            user.MustEnrollTotp = false;
        }

        return Task.CompletedTask;
    }

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
        => Task.FromResult(true);

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
        return Task.FromResult(user.EmailAddress);
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
        => Task.FromResult(true);

    public Task SetPhoneNumberConfirmedAsync(Person user, bool confirmed, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task AddOrUpdatePasskeyAsync(Person user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(passkey);

        UserPasskeyInfo? existing = await FindPasskeyAsync(user, passkey.CredentialId, cancellationToken).ConfigureAwait(false);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            const string updateSql = """
                UPDATE passkey_credential SET
                    signature_counter       = @SignatureCounter,
                    credential_display_name = @CredentialDisplayName,
                    is_backed_up            = @IsBackedUp,
                    is_user_verified        = @IsUserVerified
                WHERE person_identifier = @PersonIdentifier
                  AND credential_id     = @CredentialId;
                """;

            await connection.ExecuteAsync(new CommandDefinition(
                updateSql,
                new
                {
                    user.PersonIdentifier,
                    CredentialId = passkey.CredentialId,
                    SignatureCounter = (long)passkey.SignCount,
                    CredentialDisplayName = passkey.Name,
                    passkey.IsBackedUp,
                    passkey.IsUserVerified,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return;
        }

        const string insertSql = """
            INSERT INTO passkey_credential (
                passkey_credential_identifier, person_identifier, credential_id, public_key,
                signature_counter, transports, credential_display_name, created_at,
                is_user_verified, is_backup_eligible, is_backed_up)
            VALUES (
                @PasskeyCredentialIdentifier, @PersonIdentifier, @CredentialId, @PublicKey,
                @SignatureCounter, @Transports, @CredentialDisplayName, @CreatedAt,
                @IsUserVerified, @IsBackupEligible, @IsBackedUp);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                PasskeyCredentialIdentifier = _identifierFactory.Create(),
                user.PersonIdentifier,
                CredentialId = passkey.CredentialId,
                PublicKey = passkey.PublicKey,
                SignatureCounter = (long)passkey.SignCount,
                Transports = JoinTransports(passkey.Transports),
                CredentialDisplayName = passkey.Name,
                passkey.CreatedAt,
                passkey.IsUserVerified,
                passkey.IsBackupEligible,
                passkey.IsBackedUp,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(Person user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        string sql = $"""
            SELECT {PasskeyColumns}
            FROM passkey_credential
            WHERE person_identifier = @PersonIdentifier
            ORDER BY created_at;
            """;

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<PasskeyCredentialRow> rows = await connection.QueryAsync<PasskeyCredentialRow>(
            new CommandDefinition(sql, new { user.PersonIdentifier }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToPasskeyInfo).ToList();
    }

    public async Task<Person?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialId);

        string sql = $"""
            SELECT {PersonColumns}
            FROM person
            JOIN passkey_credential ON passkey_credential.person_identifier = person.person_identifier
            WHERE passkey_credential.credential_id = @CredentialId;
            """;

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Person>(new CommandDefinition(
            sql, new { CredentialId = credentialId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<UserPasskeyInfo?> FindPasskeyAsync(Person user, byte[] credentialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credentialId);

        string sql = $"""
            SELECT {PasskeyColumns}
            FROM passkey_credential
            WHERE person_identifier = @PersonIdentifier
              AND credential_id     = @CredentialId;
            """;

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        PasskeyCredentialRow? row = await connection.QuerySingleOrDefaultAsync<PasskeyCredentialRow>(
            new CommandDefinition(
                sql,
                new { user.PersonIdentifier, CredentialId = credentialId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : ToPasskeyInfo(row);
    }

    public async Task RemovePasskeyAsync(Person user, byte[] credentialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credentialId);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM passkey_credential WHERE person_identifier = @PersonIdentifier AND credential_id = @CredentialId;",
            new { user.PersonIdentifier, CredentialId = credentialId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static UserPasskeyInfo ToPasskeyInfo(PasskeyCredentialRow row) =>
        new(
            row.CredentialId,
            row.PublicKey,
            row.CreatedAt,
            (uint)row.SignatureCounter,
            SplitTransports(row.Transports),
            row.IsUserVerified,
            row.IsBackupEligible,
            row.IsBackedUp,

            attestationObject: [],
            clientDataJson: [])
        {
            Name = row.CredentialDisplayName,
        };

    private static string? JoinTransports(string[]? transports)
        => transports is { Length: > 0 } ? string.Join(',', transports) : null;

    private static string[]? SplitTransports(string? transports)
        => string.IsNullOrEmpty(transports)
            ? null
            : transports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class PasskeyCredentialRow
    {
        public byte[] CredentialId { get; set; } = [];
        public byte[] PublicKey { get; set; } = [];
        public long SignatureCounter { get; set; }
        public string? Transports { get; set; }
        public string? CredentialDisplayName { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsUserVerified { get; set; }
        public bool IsBackupEligible { get; set; }
        public bool IsBackedUp { get; set; }
    }

    public void Dispose()
    {
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
