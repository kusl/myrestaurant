using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="DapperAccountAdministration"/> (account administration,
/// TECHNICAL_SPECIFICATION §3.7) against a real PostgreSQL 17 container. They pin the behaviours that
/// make the slice correct: staff creation writes an account with <c>must_change_password</c> plus its
/// role grants and audit rows in one go; grant/revoke are idempotent, rotate the subject's security
/// stamp (§3.1), and audit; the last administrator can be neither un-roled nor deactivated; and a
/// credential reset stores the temporary password, forces a change, and — only when TOTP was enrolled —
/// clears the authenticator and recovery codes, forcing re-enrolment, auditing both actions.
///
/// <para>Every operation is global to the auth tables (role counts, uniqueness), so — like the
/// bootstrap tests — the tables are truncated before each test. xUnit builds a fresh instance per test
/// method and runs them sequentially, so <see cref="InitializeAsync"/> gives each a clean database.
/// Own <c>IClassFixture</c> (own container); if no container engine is available, every test skips.</para>
/// </summary>
public sealed class AccountAdministrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    // A plausibly-shaped Argon2id PHC string. The service stores the caller-supplied hash verbatim
    // (§3.2/§3.7), so the exact bytes never matter — only that they round-trip.
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$b3JpZ2luYWxvcmlnaW5hbG9yaWdpbg";

    private const string ResetPasswordHash =
        "$argon2id$v=19$m=19456,t=2,p=1$cmVzZXRyZXNldHJlc2V0cg$cmVzZXR0YWdyZXNldHRhZ3Jlc2V0dA";

    // The stored role names (the person_role.role_name CHECK values, §3.7). Spelled here as literals
    // because the RestaurantRoles constants live in the web layer, which this data-layer test project
    // does not (and must not) reference.
    private const string Administrator = "administrator";
    private const string Counter = "counter";
    private const string Kitchen = "kitchen";

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public AccountAdministrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is null)
        {
            return;
        }

        new SchemaMigrationRunner(_fixture.ConnectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        }.Run();

        _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "TRUNCATE TABLE person, person_role, passkey_credential, totp_recovery_code, security_event CASCADE;",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateStaffAsync_WritesAccountForcingPasswordChange_GrantsRoles_AndAuditsAll()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        Guid staffId = _identifiers.Create();

        CreateStaffStatus status = await administration.CreateStaffAsync(
            new NewStaffAccount(staffId, "cook", "The Cook", SamplePasswordHash, [Kitchen]),
            adminId,
            cancellationToken);

        Assert.Equal(CreateStaffStatus.Created, status);

        PersonRow staff = await ReadPersonAsync(staffId, cancellationToken);
        Assert.Equal("cook", staff.Username);
        Assert.Equal(SamplePasswordHash, staff.PasswordHash);
        Assert.True(staff.MustChangePassword);
        Assert.False(staff.MustEnrollTotp);
        Assert.Null(staff.TotpSecretProtected);
        Assert.True(staff.IsActive);
        Assert.NotEqual(Guid.Empty, staff.SecurityStamp);

        Assert.Equal(1, await CountRoleAsync(staffId, Kitchen, cancellationToken));
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.AccountCreated, adminId, cancellationToken));
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.RoleGranted, adminId, cancellationToken));
    }

    [Fact]
    public async Task CreateStaffAsync_WhenUsernameTaken_ReturnsUsernameTaken_AndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        int peopleBefore = await CountPeopleAsync(cancellationToken);

        // "owner" already exists (the seeded administrator).
        CreateStaffStatus status = await administration.CreateStaffAsync(
            new NewStaffAccount(_identifiers.Create(), "owner", null, SamplePasswordHash, [Counter]),
            adminId,
            cancellationToken);

        Assert.Equal(CreateStaffStatus.UsernameTaken, status);
        Assert.Equal(peopleBefore, await CountPeopleAsync(cancellationToken));
        Assert.Equal(0, await CountEventAsync(adminId, SecurityEventType.RoleGranted, adminId, cancellationToken));
    }

    [Fact]
    public async Task GrantRoleAsync_GrantsRotatesStampAndAudits_ThenAlreadyHeldIsANoOp()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        Guid staffId = await SeedPersonAsync("server", cancellationToken);
        Guid stampBefore = await ReadStampAsync(staffId, cancellationToken);

        RoleGrantOutcome first = await administration.GrantRoleAsync(
            staffId, Counter, adminId, cancellationToken);

        Assert.Equal(RoleGrantOutcome.Granted, first);
        Assert.Equal(1, await CountRoleAsync(staffId, Counter, cancellationToken));
        Guid stampAfterGrant = await ReadStampAsync(staffId, cancellationToken);
        Assert.NotEqual(stampBefore, stampAfterGrant);
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.RoleGranted, adminId, cancellationToken));

        RoleGrantOutcome second = await administration.GrantRoleAsync(
            staffId, Counter, adminId, cancellationToken);

        Assert.Equal(RoleGrantOutcome.AlreadyHeld, second);
        Assert.Equal(1, await CountRoleAsync(staffId, Counter, cancellationToken));
        Assert.Equal(stampAfterGrant, await ReadStampAsync(staffId, cancellationToken)); // unchanged
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.RoleGranted, adminId, cancellationToken));
    }

    [Fact]
    public async Task RevokeRoleAsync_RevokesRotatesStampAndAudits_ThenNotHeldIsANoOp()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        Guid staffId = await SeedPersonAsync("server", cancellationToken);
        await GrantRoleDirectAsync(staffId, Counter, adminId, cancellationToken);
        Guid stampBefore = await ReadStampAsync(staffId, cancellationToken);

        RoleRevokeOutcome first = await administration.RevokeRoleAsync(
            staffId, Counter, adminId, cancellationToken);

        Assert.Equal(RoleRevokeOutcome.Revoked, first);
        Assert.Equal(0, await CountRoleAsync(staffId, Counter, cancellationToken));
        Assert.NotEqual(stampBefore, await ReadStampAsync(staffId, cancellationToken));
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.RoleRevoked, adminId, cancellationToken));

        RoleRevokeOutcome second = await administration.RevokeRoleAsync(
            staffId, Counter, adminId, cancellationToken);

        Assert.Equal(RoleRevokeOutcome.NotHeld, second);
    }

    [Fact]
    public async Task RevokeRoleAsync_LastAdministrator_IsRefused_ThenAllowedOnceAnotherExists()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid firstAdmin = await SeedAdministratorAsync("owner", cancellationToken);

        RoleRevokeOutcome refused = await administration.RevokeRoleAsync(
            firstAdmin, Administrator, firstAdmin, cancellationToken);

        Assert.Equal(RoleRevokeOutcome.WouldRemoveLastAdministrator, refused);
        Assert.Equal(1, await CountRoleAsync(firstAdmin, Administrator, cancellationToken));

        // A second administrator now exists, so removing the first is allowed.
        Guid secondAdmin = await SeedAdministratorAsync("deputy", cancellationToken);

        RoleRevokeOutcome allowed = await administration.RevokeRoleAsync(
            firstAdmin, Administrator, secondAdmin, cancellationToken);

        Assert.Equal(RoleRevokeOutcome.Revoked, allowed);
        Assert.Equal(0, await CountRoleAsync(firstAdmin, Administrator, cancellationToken));
    }

    [Fact]
    public async Task ResetCredentialsAsync_WithoutAuthenticator_SetsTemporaryPassword_AndForcesChangeOnly()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        Guid staffId = await SeedPersonAsync("server", cancellationToken);
        Guid stampBefore = await ReadStampAsync(staffId, cancellationToken);

        CredentialResetResult result = await administration.ResetCredentialsAsync(
            staffId, ResetPasswordHash, adminId, cancellationToken);

        Assert.Equal(CredentialResetOutcome.Reset, result.Outcome);
        Assert.False(result.ClearedAuthenticator);

        PersonRow staff = await ReadPersonAsync(staffId, cancellationToken);
        Assert.Equal(ResetPasswordHash, staff.PasswordHash);
        Assert.True(staff.MustChangePassword);
        Assert.False(staff.MustEnrollTotp);
        Assert.Null(staff.TotpSecretProtected);
        Assert.NotEqual(stampBefore, staff.SecurityStamp);

        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.PasswordResetByAdministrator, adminId, cancellationToken));
        Assert.Equal(0, await CountEventAsync(staffId, SecurityEventType.TotpClearedByAdministrator, adminId, cancellationToken));
    }

    [Fact]
    public async Task ResetCredentialsAsync_WithAuthenticator_ClearsSecretAndRecoveryCodes_AndAuditsBoth()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        Guid staffId = await SeedPersonAsync("server", cancellationToken, totpSecretProtected: "protected-secret-blob");
        await SeedRecoveryCodesAsync(staffId, 10, cancellationToken);

        Assert.Equal(10, await CountRecoveryCodesAsync(staffId, cancellationToken));

        CredentialResetResult result = await administration.ResetCredentialsAsync(
            staffId, ResetPasswordHash, adminId, cancellationToken);

        Assert.Equal(CredentialResetOutcome.Reset, result.Outcome);
        Assert.True(result.ClearedAuthenticator);

        PersonRow staff = await ReadPersonAsync(staffId, cancellationToken);
        Assert.Equal(ResetPasswordHash, staff.PasswordHash);
        Assert.True(staff.MustChangePassword);
        Assert.True(staff.MustEnrollTotp);
        Assert.Null(staff.TotpSecretProtected);
        Assert.Equal(0, await CountRecoveryCodesAsync(staffId, cancellationToken));

        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.PasswordResetByAdministrator, adminId, cancellationToken));
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.TotpClearedByAdministrator, adminId, cancellationToken));
    }

    [Fact]
    public async Task ResetCredentialsAsync_UnknownPerson_ReturnsPersonNotFound()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);

        CredentialResetResult result = await administration.ResetCredentialsAsync(
            _identifiers.Create(), ResetPasswordHash, adminId, cancellationToken);

        Assert.Equal(CredentialResetOutcome.PersonNotFound, result.Outcome);
        Assert.False(result.ClearedAuthenticator);
    }

    [Fact]
    public async Task SetAccountActiveAsync_DeactivateThenReactivate_RotatesStamp_AndAudits()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid adminId = await SeedAdministratorAsync("owner", cancellationToken);
        Guid staffId = await SeedPersonAsync("server", cancellationToken);
        Guid stampBefore = await ReadStampAsync(staffId, cancellationToken);

        AccountActivationOutcome deactivated = await administration.SetAccountActiveAsync(
            staffId, isActive: false, adminId, cancellationToken);

        Assert.Equal(AccountActivationOutcome.Changed, deactivated);
        Assert.False((await ReadPersonAsync(staffId, cancellationToken)).IsActive);
        Assert.NotEqual(stampBefore, await ReadStampAsync(staffId, cancellationToken));
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.AccountDeactivated, adminId, cancellationToken));

        AccountActivationOutcome again = await administration.SetAccountActiveAsync(
            staffId, isActive: false, adminId, cancellationToken);
        Assert.Equal(AccountActivationOutcome.NoChange, again);

        AccountActivationOutcome reactivated = await administration.SetAccountActiveAsync(
            staffId, isActive: true, adminId, cancellationToken);

        Assert.Equal(AccountActivationOutcome.Changed, reactivated);
        Assert.True((await ReadPersonAsync(staffId, cancellationToken)).IsActive);
        Assert.Equal(1, await CountEventAsync(staffId, SecurityEventType.AccountReactivated, adminId, cancellationToken));
    }

    [Fact]
    public async Task SetAccountActiveAsync_LastActiveAdministrator_CannotBeDeactivated()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperAccountAdministration administration = Build();

        Guid firstAdmin = await SeedAdministratorAsync("owner", cancellationToken);

        AccountActivationOutcome refused = await administration.SetAccountActiveAsync(
            firstAdmin, isActive: false, firstAdmin, cancellationToken);

        Assert.Equal(AccountActivationOutcome.WouldDeactivateLastAdministrator, refused);
        Assert.True((await ReadPersonAsync(firstAdmin, cancellationToken)).IsActive);

        // With a second active administrator, deactivating the first is allowed.
        Guid secondAdmin = await SeedAdministratorAsync("deputy", cancellationToken);

        AccountActivationOutcome allowed = await administration.SetAccountActiveAsync(
            firstAdmin, isActive: false, secondAdmin, cancellationToken);

        Assert.Equal(AccountActivationOutcome.Changed, allowed);
        Assert.False((await ReadPersonAsync(firstAdmin, cancellationToken)).IsActive);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperAccountAdministration Build() => new(_connectionFactory!, _clock, _identifiers);

    /// <summary>Seeds an active administrator (person + self-granted administrator role) and returns its id.</summary>
    private async Task<Guid> SeedAdministratorAsync(string username, CancellationToken cancellationToken)
    {
        Guid id = await SeedPersonAsync(username, cancellationToken);
        await GrantRoleDirectAsync(id, Administrator, id, cancellationToken);
        return id;
    }

    /// <summary>Seeds a bare active person (a password, no roles, no obligations) and returns its id.</summary>
    private async Task<Guid> SeedPersonAsync(string username, CancellationToken cancellationToken, string? totpSecretProtected = null)
    {
        Guid id = _identifiers.Create();
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person (
                person_identifier, username, display_name, email_address, phone_number,
                password_hash, totp_secret_protected, must_change_password, must_enroll_totp,
                security_stamp, failed_access_count, lockout_end_at, is_active, created_at)
            VALUES (
                @Id, @Username, NULL, NULL, NULL,
                @PasswordHash, @TotpSecretProtected, false, false,
                @Stamp, 0, NULL, true, @CreatedAt);
            """,
            new
            {
                Id = id,
                Username = username,
                PasswordHash = SamplePasswordHash,
                TotpSecretProtected = totpSecretProtected,
                Stamp = Guid.NewGuid(),
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken));
        return id;
    }

    private async Task GrantRoleDirectAsync(Guid personId, string role, Guid grantedBy, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person_role (
                person_role_identifier, person_identifier, role_name, granted_by_person_identifier, granted_at)
            VALUES (@RoleId, @PersonId, @Role, @GrantedBy, @GrantedAt);
            """,
            new
            {
                RoleId = _identifiers.Create(),
                PersonId = personId,
                Role = role,
                GrantedBy = grantedBy,
                GrantedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken));
    }

    private async Task SeedRecoveryCodesAsync(Guid personId, int count, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        for (int index = 0; index < count; index++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO totp_recovery_code (totp_recovery_code_identifier, person_identifier, code_hash, created_at)
                VALUES (@Id, @PersonId, @Hash, @CreatedAt);
                """,
                new
                {
                    Id = _identifiers.Create(),
                    PersonId = personId,
                    Hash = Sha256Hashing.Hash($"recovery-code-{index}"),
                    CreatedAt = _clock.UtcNow,
                },
                cancellationToken: cancellationToken));
        }
    }

    private async Task<PersonRow> ReadPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<PersonRow>(new CommandDefinition(
            """
            SELECT username AS Username, password_hash AS PasswordHash, totp_secret_protected AS TotpSecretProtected,
                   must_change_password AS MustChangePassword, must_enroll_totp AS MustEnrollTotp,
                   is_active AS IsActive, security_stamp AS SecurityStamp
            FROM person WHERE person_identifier = @Id;
            """,
            new { Id = personId }, cancellationToken: cancellationToken));
    }

    private async Task<Guid> ReadStampAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT security_stamp FROM person WHERE person_identifier = @Id;",
            new { Id = personId }, cancellationToken: cancellationToken));
    }

    private async Task<int> CountRoleAsync(Guid personId, string role, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM person_role WHERE person_identifier = @Id AND role_name = @Role;",
            new { Id = personId, Role = role }, cancellationToken: cancellationToken));
    }

    private async Task<int> CountEventAsync(Guid subject, string eventType, Guid actor, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM security_event
            WHERE subject_person_identifier = @Subject AND event_type = @Type AND actor_person_identifier = @Actor;
            """,
            new { Subject = subject, Type = eventType, Actor = actor }, cancellationToken: cancellationToken));
    }

    private async Task<int> CountRecoveryCodesAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM totp_recovery_code WHERE person_identifier = @Id;",
            new { Id = personId }, cancellationToken: cancellationToken));
    }

    private async Task<int> CountPeopleAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM person;", cancellationToken: cancellationToken));
    }

    // Plain mutable POCO so Dapper's default property mapping applies; every SELECT aliases its
    // snake_case columns to these PascalCase names.
    private sealed class PersonRow
    {
        public string Username { get; set; } = string.Empty;
        public string? PasswordHash { get; set; }
        public string? TotpSecretProtected { get; set; }
        public bool MustChangePassword { get; set; }
        public bool MustEnrollTotp { get; set; }
        public bool IsActive { get; set; }
        public Guid SecurityStamp { get; set; }
    }
}
