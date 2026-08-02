using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="DapperGuestRegistration"/> (the <c>/register</c> commit,
/// TECHNICAL_SPECIFICATION §4.3, §11.1) against a real PostgreSQL 17 container.
///
/// <para>What they pin, in the order the properties matter: a passkey-only registration really does
/// write a person with a NULL <c>password_hash</c> (§3.2 permits it and §4.3 makes it the default
/// offer, so a NOT NULL slipping in would break the passkey-first path); a password-only registration
/// writes no credential row and no <c>passkey_registered</c> event; the account carries no role and
/// neither obligation flag, which is what distinguishes a guest from a staff account created by an
/// administrator (§3.7, §3.5); the audit rows name the subject as their own actor by carrying NULL,
/// matching <c>/setup</c>'s self-actions (§3.6); a taken username is reported rather than thrown and
/// leaves nothing behind; and an account with no credential at all is refused before any SQL runs.</para>
///
/// <para>Unlike <see cref="FirstAdministratorBootstrapTests"/> there is no global precondition here, so
/// these do not truncate: every test mints its own username and person identifier and asserts only on
/// its own rows, which keeps them independent of order and of whatever else has run against this
/// container. Own <see cref="PostgreSqlFixture"/>; if no container engine is available, every test
/// skips.</para>
/// </summary>
public sealed class GuestRegistrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    // A plausibly-shaped Argon2id PHC string. The service stores what the caller hashed and never
    // hashes anything itself (§3.2/§4.3), so the exact bytes never matter — only that they round-trip.
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=19456,t=2,p=1$Z3Vlc3RndWVzdGd1ZXN0Zw$Z3Vlc3RoYXNoZ3Vlc3RoYXNoZ3Vlc3Q";

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 3, 4, 18, 30, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public GuestRegistrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is null)
        {
            return ValueTask.CompletedTask;
        }

        // Idempotent: brings the schema (including migration 0002's WebAuthn columns) up.
        new SchemaMigrationRunner(_fixture.ConnectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        }.Run();

        _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegisterAsync_WithAPasskeyAndNoPassword_WritesAPasskeyOnlyAccount()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid personIdentifier = _identifiers.Create();
        string username = UniqueUsername("passkeyonly");
        UserPasskeyInfo passkey = MakePasskey(
            credentialId: Bytes(0x11, 16),
            publicKey: Bytes(0x22, 32),
            signCount: 3,
            transports: ["internal", "hybrid"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: true,
            name: "Guest's phone");

        GuestRegistrationStatus status = await BuildRegistration().RegisterAsync(
            new NewGuestAccount(personIdentifier, username, "Hungry Guest", PasswordHash: null, passkey),
            cancellationToken);

        Assert.Equal(GuestRegistrationStatus.Registered, status);

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);

        // (a) The person row. The NULL password hash is the assertion: §3.2 makes the column nullable
        // precisely so a passkey can be the only credential, and §4.3 offers exactly that first.
        PersonRow person = await ReadPersonAsync(connection, personIdentifier, cancellationToken);

        Assert.Equal(username, person.Username);
        Assert.Equal("Hungry Guest", person.DisplayName);
        Assert.Null(person.PasswordHash);
        Assert.Null(person.TotpSecretProtected);
        Assert.Null(person.EmailAddress);
        Assert.Null(person.PhoneNumber);
        Assert.False(person.MustChangePassword);
        Assert.False(person.MustEnrollTotp);
        Assert.True(person.IsActive);
        Assert.NotEqual(Guid.Empty, person.SecurityStamp);

        // (b) The credential, including the three WebAuthn flags assertion reads back (migration 0002)
        // and the comma-joined transports.
        PasskeyRow stored = await connection.QuerySingleAsync<PasskeyRow>(new CommandDefinition(
            """
            SELECT credential_id AS CredentialId, public_key AS PublicKey,
                   signature_counter AS SignatureCounter, transports AS Transports,
                   credential_display_name AS CredentialDisplayName,
                   is_user_verified AS IsUserVerified, is_backup_eligible AS IsBackupEligible,
                   is_backed_up AS IsBackedUp
            FROM passkey_credential WHERE person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken));

        Assert.Equal(Bytes(0x11, 16), stored.CredentialId);
        Assert.Equal(Bytes(0x22, 32), stored.PublicKey);
        Assert.Equal(3L, stored.SignatureCounter);
        Assert.Equal("internal,hybrid", stored.Transports);
        Assert.Equal("Guest's phone", stored.CredentialDisplayName);
        Assert.True(stored.IsUserVerified);
        Assert.True(stored.IsBackupEligible);
        Assert.True(stored.IsBackedUp);

        // (c) A guest is the absence of a role grant (§3.7) — not a role named "guest".
        Assert.Equal(0, await CountRolesAsync(connection, personIdentifier, cancellationToken));

        // (d) Two audit rows, both self-actions with a NULL actor (§3.6's shape, §3.7's vocabulary).
        List<EventRow> events = await ReadEventsAsync(connection, personIdentifier, cancellationToken);

        Assert.Equal(2, events.Count);
        Assert.All(events, row => Assert.Null(row.Actor));
        Assert.Contains(events, row => row.EventType == SecurityEventType.AccountCreated);
        Assert.Contains(events, row => row.EventType == SecurityEventType.PasskeyRegistered);
    }

    [Fact]
    public async Task RegisterAsync_WithAPasswordAndNoPasskey_WritesNoCredentialRowAndNoPasskeyEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid personIdentifier = _identifiers.Create();
        string username = UniqueUsername("passwordonly");

        GuestRegistrationStatus status = await BuildRegistration().RegisterAsync(
            new NewGuestAccount(personIdentifier, username, DisplayName: null, SamplePasswordHash, Passkey: null),
            cancellationToken);

        Assert.Equal(GuestRegistrationStatus.Registered, status);

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);

        PersonRow person = await ReadPersonAsync(connection, personIdentifier, cancellationToken);
        Assert.Equal(SamplePasswordHash, person.PasswordHash);
        Assert.Null(person.DisplayName);
        Assert.False(person.MustChangePassword);

        // The distinction from a staff account (§3.7): no forced change, so nothing handed this person
        // a temporary password — they chose their own.
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM passkey_credential WHERE person_identifier = @Id;",
            new { Id = personIdentifier }, cancellationToken: cancellationToken)));

        EventRow only = Assert.Single(await ReadEventsAsync(connection, personIdentifier, cancellationToken));
        Assert.Equal(SecurityEventType.AccountCreated, only.EventType);
        Assert.Null(only.Actor);
    }

    [Fact]
    public async Task RegisterAsync_WithBothCredentials_KeepsBoth()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid personIdentifier = _identifiers.Create();
        string username = UniqueUsername("both");

        // §3.3: the passkey is "always offered, never required" — a guest who set a password and then
        // added a passkey anyway keeps the password as a backup rather than trading one for the other.
        GuestRegistrationStatus status = await BuildRegistration().RegisterAsync(
            new NewGuestAccount(
                personIdentifier, username, "Belt And Braces", SamplePasswordHash, MakePasskey(Bytes(0x33, 16))),
            cancellationToken);

        Assert.Equal(GuestRegistrationStatus.Registered, status);

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);

        PersonRow person = await ReadPersonAsync(connection, personIdentifier, cancellationToken);
        Assert.Equal(SamplePasswordHash, person.PasswordHash);

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM passkey_credential WHERE person_identifier = @Id;",
            new { Id = personIdentifier }, cancellationToken: cancellationToken)));

        Assert.Equal(2, (await ReadEventsAsync(connection, personIdentifier, cancellationToken)).Count);
    }

    [Fact]
    public async Task RegisterAsync_WhenTheUsernameIsTaken_ReportsItAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        IGuestRegistration registration = BuildRegistration();
        string username = UniqueUsername("contested");

        Assert.Equal(
            GuestRegistrationStatus.Registered,
            await registration.RegisterAsync(
                new NewGuestAccount(_identifiers.Create(), username, null, SamplePasswordHash, null),
                cancellationToken));

        // The loser of the race: a different person identifier, the same name, and a passkey that must
        // not survive the rolled-back transaction.
        Guid loserIdentifier = _identifiers.Create();
        GuestRegistrationStatus second = await registration.RegisterAsync(
            new NewGuestAccount(loserIdentifier, username, null, SamplePasswordHash, MakePasskey(Bytes(0x44, 16))),
            cancellationToken);

        Assert.Equal(GuestRegistrationStatus.UsernameTaken, second);

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);

        Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM person WHERE person_identifier = @Id);",
            new { Id = loserIdentifier }, cancellationToken: cancellationToken)));
        Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM passkey_credential WHERE person_identifier = @Id);",
            new { Id = loserIdentifier }, cancellationToken: cancellationToken)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM person WHERE username = @Username::citext;",
            new { Username = username }, cancellationToken: cancellationToken)));
    }

    [Fact]
    public async Task RegisterAsync_WhenTheUsernameDiffersOnlyByCase_IsStillTaken()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        IGuestRegistration registration = BuildRegistration();
        string username = UniqueUsername("mixedcase");

        Assert.Equal(
            GuestRegistrationStatus.Registered,
            await registration.RegisterAsync(
                new NewGuestAccount(_identifiers.Create(), username, null, SamplePasswordHash, null),
                cancellationToken));

        // §3.1 stores usernames as citext, so uniqueness is case-insensitive in the database rather
        // than in a shadow column the application has to remember to normalize.
        GuestRegistrationStatus second = await registration.RegisterAsync(
            new NewGuestAccount(
                _identifiers.Create(), username.ToUpperInvariant(), null, SamplePasswordHash, null),
            cancellationToken);

        Assert.Equal(GuestRegistrationStatus.UsernameTaken, second);
    }

    [Fact]
    public async Task RegisterAsync_WithNoCredentialAtAll_IsRefusedBeforeAnySql()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid personIdentifier = _identifiers.Create();
        string username = UniqueUsername("credentialless");

        // The surface never offers this — the "not now" button is rendered only when a password was
        // set — so it is a caller bug rather than a user error, and it fails loudly rather than
        // creating an account nobody could ever sign into.
        await Assert.ThrowsAsync<ArgumentException>(() => BuildRegistration().RegisterAsync(
            new NewGuestAccount(personIdentifier, username, "Nobody", PasswordHash: null, Passkey: null),
            cancellationToken));

        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM person WHERE person_identifier = @Id);",
            new { Id = personIdentifier }, cancellationToken: cancellationToken)));
    }

    [Fact]
    public async Task RegisterAsync_ProducesAnAccountTheIdentityStoreCanSignIn()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid personIdentifier = _identifiers.Create();
        string username = UniqueUsername("roundtrip");
        byte[] credentialId = Bytes(0x55, 16);

        Assert.Equal(
            GuestRegistrationStatus.Registered,
            await BuildRegistration().RegisterAsync(
                new NewGuestAccount(personIdentifier, username, "Round Trip", null, MakePasskey(credentialId)),
                cancellationToken));

        // The registration path writes rows; the sign-in path reads them through DapperUserStore. This
        // is the seam where a column the service filled differently from the store's expectation would
        // show up — and for a passkey-only guest it is the only way in, so it is worth proving here
        // rather than discovering it in a browser.
        DapperUserStore store = BuildStore();

        Person? guest = await store.FindByNameAsync(username, cancellationToken);
        Assert.NotNull(guest);
        Assert.Equal(personIdentifier, guest!.PersonIdentifier);
        Assert.Equal("Round Trip", guest.DisplayName);
        Assert.Null(guest.PasswordHash);
        Assert.True(guest.IsActive);

        UserPasskeyInfo? found = await store.FindPasskeyAsync(guest, credentialId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(credentialId, found!.CredentialId);

        // Two-factor is derived from the absence of a TOTP secret (§3.4), so a guest is not challenged.
        Assert.False(await store.GetTwoFactorEnabledAsync(guest, cancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperGuestRegistration BuildRegistration()
        => new(_connectionFactory!, _clock, _identifiers);

    private DapperUserStore BuildStore()
        => new(_connectionFactory!, _clock, _identifiers, new EphemeralDataProtectionProvider(), new IdentityErrorDescriber());

    private static async Task<PersonRow> ReadPersonAsync(
        DbConnection connection, Guid personIdentifier, CancellationToken cancellationToken)
        => await connection.QuerySingleAsync<PersonRow>(new CommandDefinition(
            """
            SELECT username AS Username, display_name AS DisplayName, email_address AS EmailAddress,
                   phone_number AS PhoneNumber, password_hash AS PasswordHash,
                   totp_secret_protected AS TotpSecretProtected,
                   must_change_password AS MustChangePassword, must_enroll_totp AS MustEnrollTotp,
                   is_active AS IsActive, security_stamp AS SecurityStamp
            FROM person WHERE person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken));

    private static async Task<int> CountRolesAsync(
        DbConnection connection, Guid personIdentifier, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM person_role WHERE person_identifier = @Id;",
            new { Id = personIdentifier }, cancellationToken: cancellationToken));

    private static async Task<List<EventRow>> ReadEventsAsync(
        DbConnection connection, Guid personIdentifier, CancellationToken cancellationToken)
        => (await connection.QueryAsync<EventRow>(new CommandDefinition(
            """
            SELECT event_type AS EventType, actor_person_identifier AS Actor
            FROM security_event WHERE subject_person_identifier = @Id;
            """,
            new { Id = personIdentifier }, cancellationToken: cancellationToken))).ToList();

    // Built the way the framework's attestation would. attestationObject / clientDataJson are set here
    // but never persisted (attestation is 'none', §3.3) — the same shape the bootstrap tests use.
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
            publicKey ?? Bytes(0x60, 16),
            _clock.UtcNow,
            signCount,
            transports,
            isUserVerified,
            isBackupEligible,
            isBackedUp,
            attestationObject: Bytes(0x70, 4),
            clientDataJson: Bytes(0x80, 4))
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
        public string? PasswordHash { get; set; }
        public string? TotpSecretProtected { get; set; }
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

    private sealed class EventRow
    {
        public string EventType { get; set; } = string.Empty;
        public Guid? Actor { get; set; }
    }
}
