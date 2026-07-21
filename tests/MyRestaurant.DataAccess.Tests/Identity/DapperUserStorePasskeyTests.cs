using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="DapperUserStore"/>'s <c>IUserPasskeyStore</c> implementation
/// (TECHNICAL_SPECIFICATION §3.3, §16.2 — "every Identity store method") against a real PostgreSQL 17
/// container, exercising the store directly rather than through the WebAuthn ceremony. They pin the two
/// behaviours that matter for correctness: every field of a <see cref="UserPasskeyInfo"/> the store
/// persists round-trips (the backup-eligible bit especially — assertion reads it), and an add-or-update
/// against an already-stored credential rewrites only the mutable fields, never the public key or the
/// backup-eligible bit captured at registration.
///
/// Separate container from <see cref="DapperUserStoreTests"/> (own <c>IClassFixture</c>); unique
/// usernames keep the cases independent. If no container engine is available, every test skips.
/// </summary>
public sealed class DapperUserStorePasskeyTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtectionProvider = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public DapperUserStorePasskeyTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is not null)
        {
            // Idempotent: brings the schema (including migration 0002) up so the table exists.
            new SchemaMigrationRunner(_fixture.ConnectionString)
            {
                MaximumAttempts = 3,
                DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
            }.Run();

            _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);
        }

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
    public async Task AddThenGetPasskeys_RoundTripsEveryStoredField()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = await CreatePersonAsync(store, "pk_roundtrip", cancellationToken);

        byte[] credentialId = Bytes(0xA1, 8);
        UserPasskeyInfo passkey = MakePasskey(
            credentialId,
            publicKey: Bytes(0xB2, 16),
            signCount: 7,
            transports: ["internal", "hybrid"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: true,
            name: "Pixel");

        await store.AddOrUpdatePasskeyAsync(person, passkey, cancellationToken);

        IList<UserPasskeyInfo> all = await store.GetPasskeysAsync(person, cancellationToken);

        UserPasskeyInfo stored = Assert.Single(all);
        Assert.Equal(credentialId, stored.CredentialId);
        Assert.Equal(passkey.PublicKey, stored.PublicKey);
        Assert.Equal(7u, stored.SignCount);
        Assert.Equal(new[] { "internal", "hybrid" }, stored.Transports);
        Assert.True(stored.IsUserVerified);
        Assert.True(stored.IsBackupEligible);
        Assert.True(stored.IsBackedUp);
        Assert.Equal("Pixel", stored.Name);
        Assert.Equal(_clock.UtcNow.UtcDateTime, stored.CreatedAt.UtcDateTime);
    }

    [Fact]
    public async Task GetPasskeys_WithNoTransports_ReturnsNull()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = await CreatePersonAsync(store, "pk_notransports", cancellationToken);
        await store.AddOrUpdatePasskeyAsync(person, MakePasskey(Bytes(0xC3, 8), transports: null), cancellationToken);

        UserPasskeyInfo stored = Assert.Single(await store.GetPasskeysAsync(person, cancellationToken));

        Assert.Null(stored.Transports);
    }

    [Fact]
    public async Task FindByPasskeyId_ReturnsTheOwningPerson()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = await CreatePersonAsync(store, "pk_owner", cancellationToken);
        byte[] credentialId = Bytes(0xD4, 8);
        await store.AddOrUpdatePasskeyAsync(person, MakePasskey(credentialId), cancellationToken);

        Person? found = await store.FindByPasskeyIdAsync(credentialId, cancellationToken);

        Assert.NotNull(found);
        Assert.Equal(person.PersonIdentifier, found!.PersonIdentifier);
    }

    [Fact]
    public async Task FindByPasskeyId_ForUnknownCredential_ReturnsNull()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person? found = await store.FindByPasskeyIdAsync(Bytes(0xEE, 8), cancellationToken);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindPasskey_ReturnsForOwner_AndNullForAnotherPerson()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person owner = await CreatePersonAsync(store, "pk_find_owner", cancellationToken);
        Person other = await CreatePersonAsync(store, "pk_find_other", cancellationToken);
        byte[] credentialId = Bytes(0x5A, 8);
        await store.AddOrUpdatePasskeyAsync(owner, MakePasskey(credentialId), cancellationToken);

        UserPasskeyInfo? forOwner = await store.FindPasskeyAsync(owner, credentialId, cancellationToken);
        UserPasskeyInfo? forOther = await store.FindPasskeyAsync(other, credentialId, cancellationToken);

        Assert.NotNull(forOwner);
        Assert.Equal(credentialId, forOwner!.CredentialId);
        Assert.Null(forOther);
    }

    [Fact]
    public async Task AddOrUpdatePasskey_OnExistingCredential_UpdatesMutableFieldsOnly()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = await CreatePersonAsync(store, "pk_update", cancellationToken);
        byte[] credentialId = Bytes(0x6B, 8);
        byte[] originalPublicKey = Bytes(0x11, 16);

        await store.AddOrUpdatePasskeyAsync(
            person,
            MakePasskey(
                credentialId,
                publicKey: originalPublicKey,
                signCount: 3,
                isUserVerified: false,
                isBackupEligible: true,
                isBackedUp: false,
                name: "Before"),
            cancellationToken);

        // Same credential id, but a later assertion: higher sign count, backup state flipped, renamed —
        // and (deliberately, to prove they are ignored) a different public key and backup-eligible bit.
        await store.AddOrUpdatePasskeyAsync(
            person,
            MakePasskey(
                credentialId,
                publicKey: Bytes(0x99, 16),
                signCount: 12,
                isUserVerified: true,
                isBackupEligible: false,
                isBackedUp: true,
                name: "After"),
            cancellationToken);

        IList<UserPasskeyInfo> all = await store.GetPasskeysAsync(person, cancellationToken);
        UserPasskeyInfo stored = Assert.Single(all);   // updated in place, not duplicated

        // Mutable fields were written:
        Assert.Equal(12u, stored.SignCount);
        Assert.True(stored.IsBackedUp);
        Assert.True(stored.IsUserVerified);
        Assert.Equal("After", stored.Name);

        // Immutable registration fields were preserved:
        Assert.Equal(originalPublicKey, stored.PublicKey);
        Assert.True(stored.IsBackupEligible);
    }

    [Fact]
    public async Task RemovePasskey_DeletesTheCredential()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = await CreatePersonAsync(store, "pk_remove", cancellationToken);
        byte[] credentialId = Bytes(0x7C, 8);
        await store.AddOrUpdatePasskeyAsync(person, MakePasskey(credentialId), cancellationToken);

        await store.RemovePasskeyAsync(person, credentialId, cancellationToken);

        Assert.Empty(await store.GetPasskeysAsync(person, cancellationToken));
        Assert.Null(await store.FindByPasskeyIdAsync(credentialId, cancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private DapperUserStore BuildStore() => new(
        _connectionFactory!,
        _clock,
        new UuidV7IdentifierFactory(),
        _dataProtectionProvider,
        new IdentityErrorDescriber());

    private async Task<Person> CreatePersonAsync(DapperUserStore store, string prefix, CancellationToken cancellationToken)
    {
        Person person = new() { Username = UniqueUsername(prefix) };
        IdentityResult created = await store.CreateAsync(person, cancellationToken);
        Assert.True(created.Succeeded);
        return person;
    }

    // Builds a UserPasskeyInfo the way the framework's attestation would. attestationObject /
    // clientDataJson are set here but the store does not persist them (attestation is 'none', §3.3).
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
}
