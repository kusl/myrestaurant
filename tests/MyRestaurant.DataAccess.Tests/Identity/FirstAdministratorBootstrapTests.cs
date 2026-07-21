using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="DapperFirstAdministratorBootstrap"/> (the <c>/setup</c> commit,
/// TECHNICAL_SPECIFICATION §3.6) against a real PostgreSQL 17 container. They pin the three things that
/// make the bootstrap correct: the unlocked <see cref="IFirstAdministratorBootstrap.AdministratorExistsAsync"/>
/// gate flips once an administrator exists; a create on an empty database writes the whole account —
/// person, passkey, ten recovery codes, the self-granted <c>administrator</c> role, and all four
/// <c>security_event</c> rows — in one go, with the TOTP secret and recovery codes round-tripping
/// through <see cref="DapperUserStore"/> (proving the bootstrap used the same at-rest encoding the sign-in
/// path reads); and a second create once an administrator already exists writes nothing and reports the
/// loss (§3.6: "one wins, the other sees 404 on retry").
///
/// <para>The bootstrap's zero-administrator condition is global, so — unlike the per-person passkey
/// tests — the auth tables are truncated before every test. xUnit builds a fresh instance of this class
/// per test method and runs the methods in a class sequentially, so <see cref="InitializeAsync"/> gives
/// each test a clean database regardless of order. Own <c>IClassFixture</c> (own container); if no
/// container engine is available, every test skips.</para>
/// </summary>
public sealed class FirstAdministratorBootstrapTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    // A plausibly-shaped Argon2id PHC string. The bootstrap stores the wizard-supplied hash verbatim
    // (it does not hash here, §3.2/§3.6), so the exact bytes never matter — only that it round-trips.
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$b3JpZ2luYWxvcmlnaW5hbG9yaWdpbg";

    // A fixed 20-byte secret (exactly 32 Base32 characters) so the round-trip assertion has a known value.
    private static readonly string TotpSecret = Base32Text.Encode(Bytes(0x5A, 20));

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtectionProvider = new();
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public FirstAdministratorBootstrapTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is null)
        {
            return;
        }

        // Idempotent: brings the schema (including migration 0002) up so the tables exist.
        new SchemaMigrationRunner(_fixture.ConnectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        }.Run();

        _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);

        // Reset to a zero-administrator state before each test. CASCADE also clears the child tables
        // (person_role, passkey_credential, totp_recovery_code, security_event) via their FKs to person.
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            TRUNCATE TABLE person, person_role, passkey_credential, totp_recovery_code, security_event CASCADE;
            """,
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
    public async Task AdministratorExistsAsync_IsFalseUntilAnAdministratorIsCreated()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperFirstAdministratorBootstrap bootstrap = BuildBootstrap();

        Assert.False(await bootstrap.AdministratorExistsAsync(cancellationToken));

        FirstAdministratorBootstrapResult result = await bootstrap.CreateFirstAdministratorAsync(
            NewAdmin(_identifiers.Create(), UniqueUsername("exists"), MakePasskey(Bytes(0xA1, 16))),
            cancellationToken);

        Assert.Equal(FirstAdministratorBootstrapStatus.Created, result.Status);
        Assert.True(await bootstrap.AdministratorExistsAsync(cancellationToken));
    }

    [Fact]
    public async Task CreateFirstAdministratorAsync_OnAnEmptyDatabase_WritesTheAccountAndSelfGrantsAdministrator()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperFirstAdministratorBootstrap bootstrap = BuildBootstrap();

        Guid personIdentifier = _identifiers.Create();
        string username = UniqueUsername("owner");
        UserPasskeyInfo passkey = MakePasskey(
            credentialId: Bytes(0xA1, 16),
            publicKey: Bytes(0xB2, 32),
            signCount: 5,
            transports: ["internal", "hybrid"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: true,
            name: "Owner's laptop");

        FirstAdministratorBootstrapResult result = await bootstrap.CreateFirstAdministratorAsync(
            NewAdmin(personIdentifier, username, passkey), cancellationToken);

        Assert.Equal(FirstAdministratorBootstrapStatus.Created, result.Status);
        Assert.Equal(10, result.RecoveryCodes.Count);
        Assert.Equal(10, result.RecoveryCodes.Distinct().Count());

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);

        // (a) The person row: obligation flags cleared (this account enrolled its own TOTP, §3.5),
        // active, no contact details, password hash stored verbatim, a real security stamp minted.
        PersonRow person = await connection.QuerySingleAsync<PersonRow>(new CommandDefinition(
            """
            SELECT username AS Username, display_name AS DisplayName, email_address AS EmailAddress,
                   phone_number AS PhoneNumber, password_hash AS PasswordHash,
                   must_change_password AS MustChangePassword, must_enroll_totp AS MustEnrollTotp,
                   is_active AS IsActive, security_stamp AS SecurityStamp
            FROM person WHERE person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken));

        Assert.Equal(username, person.Username);
        Assert.Equal("Restaurant Owner", person.DisplayName);
        Assert.Null(person.EmailAddress);
        Assert.Null(person.PhoneNumber);
        Assert.Equal(SamplePasswordHash, person.PasswordHash);
        Assert.False(person.MustChangePassword);
        Assert.False(person.MustEnrollTotp);
        Assert.True(person.IsActive);
        Assert.NotEqual(Guid.Empty, person.SecurityStamp);

        // (b) The passkey row: every registration field the bootstrap carried over, including the three
        // WebAuthn flags (migration 0002) and the comma-joined transports.
        PasskeyRow storedPasskey = await connection.QuerySingleAsync<PasskeyRow>(new CommandDefinition(
            """
            SELECT credential_id AS CredentialId, public_key AS PublicKey, signature_counter AS SignatureCounter,
                   transports AS Transports, credential_display_name AS CredentialDisplayName,
                   is_user_verified AS IsUserVerified, is_backup_eligible AS IsBackupEligible, is_backed_up AS IsBackedUp
            FROM passkey_credential WHERE person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken));

        Assert.Equal(Bytes(0xA1, 16), storedPasskey.CredentialId);
        Assert.Equal(Bytes(0xB2, 32), storedPasskey.PublicKey);
        Assert.Equal(5L, storedPasskey.SignatureCounter);
        Assert.Equal("internal,hybrid", storedPasskey.Transports);
        Assert.Equal("Owner's laptop", storedPasskey.CredentialDisplayName);
        Assert.True(storedPasskey.IsUserVerified);
        Assert.True(storedPasskey.IsBackupEligible);
        Assert.True(storedPasskey.IsBackedUp);

        // (c) The administrator grant, recorded as its own grantor (§3.6).
        RoleRow role = await connection.QuerySingleAsync<RoleRow>(new CommandDefinition(
            """
            SELECT role_name AS RoleName, granted_by_person_identifier AS GrantedBy
            FROM person_role WHERE person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken));

        Assert.Equal("administrator", role.RoleName);
        Assert.Equal(personIdentifier, role.GrantedBy);

        // (d) The audit trail (§3.7): four events, all about this person; the self-actions carry a NULL
        // actor and the role grant records the new administrator as their own actor.
        List<EventRow> events = (await connection.QueryAsync<EventRow>(new CommandDefinition(
            """
            SELECT event_type AS EventType, subject_person_identifier AS Subject, actor_person_identifier AS Actor
            FROM security_event WHERE subject_person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken))).ToList();

        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal(personIdentifier, e.Subject));

        EventRow accountCreated = Assert.Single(events, e => e.EventType == SecurityEventType.AccountCreated);
        EventRow passkeyRegistered = Assert.Single(events, e => e.EventType == SecurityEventType.PasskeyRegistered);
        EventRow totpEnrolled = Assert.Single(events, e => e.EventType == SecurityEventType.TotpEnrolled);
        EventRow roleGranted = Assert.Single(events, e => e.EventType == SecurityEventType.RoleGranted);

        Assert.Null(accountCreated.Actor);
        Assert.Null(passkeyRegistered.Actor);
        Assert.Null(totpEnrolled.Actor);
        Assert.Equal(personIdentifier, roleGranted.Actor);

        // (e) The TOTP secret and recovery codes round-trip through the store, which reads them the way
        // the sign-in path does. GetAuthenticatorKeyAsync returning the original secret proves the
        // bootstrap protected it under the encoding the store unprotects with; redeeming one of the
        // returned plaintext codes proves those codes are exactly the ones persisted (as hashes) and
        // are single-use.
        DapperUserStore store = BuildStore();
        Person? admin = await store.FindByNameAsync(username, cancellationToken);
        Assert.NotNull(admin);
        Assert.Equal(personIdentifier, admin!.PersonIdentifier);
        Assert.Equal(TotpSecret, await store.GetAuthenticatorKeyAsync(admin, cancellationToken));

        Assert.Equal(10, await store.CountCodesAsync(admin, cancellationToken));
        Assert.True(await store.RedeemCodeAsync(admin, result.RecoveryCodes[0], cancellationToken));
        Assert.Equal(9, await store.CountCodesAsync(admin, cancellationToken));
    }

    [Fact]
    public async Task CreateFirstAdministratorAsync_WhenAnAdministratorAlreadyExists_WritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperFirstAdministratorBootstrap bootstrap = BuildBootstrap();

        FirstAdministratorBootstrapResult first = await bootstrap.CreateFirstAdministratorAsync(
            NewAdmin(_identifiers.Create(), UniqueUsername("first"), MakePasskey(Bytes(0xA1, 16))),
            cancellationToken);
        Assert.Equal(FirstAdministratorBootstrapStatus.Created, first.Status);

        // A second, fully-formed candidate: it got past the unlocked gate in a racing browser, then lost
        // the re-check under the advisory lock.
        string loserUsername = UniqueUsername("loser");
        FirstAdministratorBootstrapResult second = await bootstrap.CreateFirstAdministratorAsync(
            NewAdmin(_identifiers.Create(), loserUsername, MakePasskey(Bytes(0xC3, 16))),
            cancellationToken);

        Assert.Equal(FirstAdministratorBootstrapStatus.AdministratorAlreadyExists, second.Status);
        Assert.Empty(second.RecoveryCodes);

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);

        // Nothing was written for the loser: still exactly one person, one administrator, and no row for
        // the losing username (the whole transaction rolled back).
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM person;", cancellationToken: cancellationToken)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM person_role WHERE role_name = 'administrator';",
            cancellationToken: cancellationToken)));
        Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM person WHERE username = @Username::citext);",
            new { Username = loserUsername }, cancellationToken: cancellationToken)));
    }

    // --- helpers -----------------------------------------------------------------------------------
    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperFirstAdministratorBootstrap BuildBootstrap()
        => new(_connectionFactory!, _clock, _identifiers, _dataProtectionProvider);

    private DapperUserStore BuildStore()
        => new(_connectionFactory!, _clock, _identifiers, _dataProtectionProvider, new IdentityErrorDescriber());

    private static NewAdministrator NewAdmin(Guid personIdentifier, string username, UserPasskeyInfo passkey)
        => new(personIdentifier, username, "Restaurant Owner", SamplePasswordHash, TotpSecret, passkey);

    // Builds a UserPasskeyInfo the way the framework's attestation would. attestationObject /
    // clientDataJson are set here but the bootstrap does not persist them (attestation is 'none', §3.3).
    private UserPasskeyInfo MakePasskey(
        byte[] credentialId,
        byte[]? publicKey = null,
        uint signCount = 0,
        string[]? transports = null,
        bool isUserVerified = false,
        bool isBackupEligible = false,
        bool isBackedUp = false,
        string? name = null)
        => new(
            credentialId,
            publicKey ?? Bytes(0x20, 16),
            _clock.UtcNow,
            signCount,
            transports,
            isUserVerified,
            isBackupEligible,
            isBackedUp,
            attestationObject: Bytes(0x30, 4),
            clientDataJson: Bytes(0x40, 4))
        {
            Name = name,
        };

    private static byte[] Bytes(byte seed, int length)
    {
        byte[] value = new byte[length];
        for (int i = 0; i < length; i++)
        {
            value[i] = (byte)(seed + i);
        }

        return value;
    }

    private static string UniqueUsername(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    // Row DTOs for the direct assertion queries. Plain mutable POCOs like Person, so Dapper's default
    // property mapping applies; every SELECT aliases its snake_case columns to these PascalCase names.
    private sealed class PersonRow
    {
        public string Username { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? EmailAddress { get; set; }
        public string? PhoneNumber { get; set; }
        public string PasswordHash { get; set; } = string.Empty;
        public bool MustChangePassword { get; set; }
        public bool MustEnrollTotp { get; set; }
        public bool IsActive { get; set; }
        public Guid SecurityStamp { get; set; }
    }

    private sealed class PasskeyRow
    {
        public byte[] CredentialId { get; set; } = [];
        public byte[] PublicKey { get; set; } = [];
        public long SignatureCounter { get; set; }
        public string? Transports { get; set; }
        public string? CredentialDisplayName { get; set; }
        public bool IsUserVerified { get; set; }
        public bool IsBackupEligible { get; set; }
        public bool IsBackedUp { get; set; }
    }

    private sealed class RoleRow
    {
        public string RoleName { get; set; } = string.Empty;
        public Guid GrantedBy { get; set; }
    }

    private sealed class EventRow
    {
        public string EventType { get; set; } = string.Empty;
        public Guid Subject { get; set; }
        public Guid? Actor { get; set; }
    }
}
