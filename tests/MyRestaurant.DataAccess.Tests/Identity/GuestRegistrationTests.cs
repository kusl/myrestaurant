using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

public sealed class GuestRegistrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
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

        Assert.Equal(0, await CountRolesAsync(connection, personIdentifier, cancellationToken));

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

        Assert.False(await store.GetTwoFactorEnabledAsync(guest, cancellationToken));
    }

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
