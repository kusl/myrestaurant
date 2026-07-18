using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="DapperUserStore"/> (TECHNICAL_SPECIFICATION §3.1, §16.2 — "every
/// Identity store method") against a real PostgreSQL 17 container. They exercise the store directly
/// (rather than through <see cref="UserManager{TUser}"/>) so each capability interface is covered
/// precisely: create/lookup with citext case-insensitivity, the DuplicateUserName / InvalidUserName
/// mappings, password get/set, security-stamp regeneration, lockout counters, TOTP-secret encryption
/// at rest, single-use hashed recovery codes, and the role read path — plus the two operations the
/// store deliberately refuses (delete; unattributed role grant/revoke).
///
/// The class shares one container per <c>IClassFixture</c>; tests use unique usernames to stay
/// independent within the shared database. If no container engine is available, every test skips.
/// </summary>
public sealed class DapperUserStoreTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtectionProvider = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public DapperUserStoreTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is not null)
        {
            // Idempotent: brings the schema up once so the store has tables to work against.
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
    public async Task CreateAsync_InsertsPerson_AndFindByIdReturnsIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new()
        {
            Username = UniqueUsername("alice"),
            DisplayName = "Alice Example",
            EmailAddress = "alice@example.com",
        };

        IdentityResult created = await store.CreateAsync(person, cancellationToken);

        Assert.True(created.Succeeded);
        Assert.NotEqual(Guid.Empty, person.PersonIdentifier);   // store assigned the id
        Assert.NotEqual(Guid.Empty, person.SecurityStamp);       // store assigned a stamp
        Assert.NotEqual(default, person.CreatedAt);              // store stamped creation time

        Person? found = await store.FindByIdAsync(person.PersonIdentifier.ToString(), cancellationToken);

        Assert.NotNull(found);
        Assert.Equal(person.PersonIdentifier, found!.PersonIdentifier);
        Assert.Equal(person.Username, found.Username);
        Assert.Equal("Alice Example", found.DisplayName);
        Assert.Equal("alice@example.com", found.EmailAddress);
        Assert.True(found.IsActive);
        Assert.False(found.MustChangePassword);
        Assert.False(found.MustEnrollTotp);
    }

    [Fact]
    public async Task FindByNameAsync_MatchesCaseInsensitively()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("Casing") };
        await store.CreateAsync(person, cancellationToken);

        // Identity passes the normalized (upper) name; citext matches regardless of case.
        Person? found = await store.FindByNameAsync(person.Username.ToUpperInvariant(), cancellationToken);

        Assert.NotNull(found);
        Assert.Equal(person.PersonIdentifier, found!.PersonIdentifier);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateUsername_FailsWithDuplicateUserName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        string username = UniqueUsername("dupe");
        IdentityResult first = await store.CreateAsync(new Person { Username = username }, cancellationToken);
        IdentityResult second = await store.CreateAsync(new Person { Username = username }, cancellationToken);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(second.Errors, error => error.Code == "DuplicateUserName");
    }

    [Fact]
    public async Task CreateAsync_WithTooShortUsername_FailsOnTheCheckConstraint()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        // 2 characters violates CHECK (char_length(username) BETWEEN 3 AND 64).
        IdentityResult result = await store.CreateAsync(new Person { Username = "ab" }, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == "InvalidUserName");
    }

    [Fact]
    public async Task PasswordHash_SetUpdateAndReRead_RoundTrips()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("pw") };
        await store.CreateAsync(person, cancellationToken);

        Assert.False(await store.HasPasswordAsync(person, cancellationToken));

        const string phc = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E$dGFndGFndGFndGFndGFndGFndGFndGFndGFndGE";
        await store.SetPasswordHashAsync(person, phc, cancellationToken);
        Assert.True((await store.UpdateAsync(person, cancellationToken)).Succeeded);

        Person reread = (await store.FindByIdAsync(person.PersonIdentifier.ToString(), cancellationToken))!;

        Assert.Equal(phc, await store.GetPasswordHashAsync(reread, cancellationToken));
        Assert.True(await store.HasPasswordAsync(reread, cancellationToken));
    }

    [Fact]
    public async Task SetSecurityStampAsync_RegeneratesAFreshGuid()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("stamp") };
        await store.CreateAsync(person, cancellationToken);

        string? before = await store.GetSecurityStampAsync(person, cancellationToken);
        await store.SetSecurityStampAsync(person, "an-opaque-identity-string-that-we-ignore", cancellationToken);
        string? after = await store.GetSecurityStampAsync(person, cancellationToken);

        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.True(Guid.TryParse(after, out _)); // still a uuid, as the column requires
    }

    [Fact]
    public async Task Lockout_CountersAndEndDate_Persist()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("lock") };
        await store.CreateAsync(person, cancellationToken);

        Assert.True(await store.GetLockoutEnabledAsync(person, cancellationToken));
        Assert.Equal(1, await store.IncrementAccessFailedCountAsync(person, cancellationToken));
        Assert.Equal(2, await store.IncrementAccessFailedCountAsync(person, cancellationToken));
        Assert.Equal(3, await store.IncrementAccessFailedCountAsync(person, cancellationToken));

        DateTimeOffset lockoutEnd = _clock.UtcNow.AddMinutes(5);
        await store.SetLockoutEndDateAsync(person, lockoutEnd, cancellationToken);
        Assert.True((await store.UpdateAsync(person, cancellationToken)).Succeeded);

        Person reread = (await store.FindByIdAsync(person.PersonIdentifier.ToString(), cancellationToken))!;
        Assert.Equal(3, await store.GetAccessFailedCountAsync(reread, cancellationToken));
        DateTimeOffset? storedEnd = await store.GetLockoutEndDateAsync(reread, cancellationToken);
        Assert.NotNull(storedEnd);
        Assert.True(Math.Abs((storedEnd!.Value - lockoutEnd).TotalSeconds) < 1);

        await store.ResetAccessFailedCountAsync(reread, cancellationToken);
        Assert.True((await store.UpdateAsync(reread, cancellationToken)).Succeeded);

        Person afterReset = (await store.FindByIdAsync(person.PersonIdentifier.ToString(), cancellationToken))!;
        Assert.Equal(0, afterReset.FailedAccessCount);
    }

    [Fact]
    public async Task Totp_AuthenticatorKeyIsEncryptedAtRest_AndDrivesTwoFactorEnabled()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("totp") };
        await store.CreateAsync(person, cancellationToken);

        Assert.False(await store.GetTwoFactorEnabledAsync(person, cancellationToken));

        const string authenticatorKey = "JBSWY3DPEHPK3PXP"; // opaque to the store; it just protects it
        await store.SetAuthenticatorKeyAsync(person, authenticatorKey, cancellationToken);
        Assert.True((await store.UpdateAsync(person, cancellationToken)).Succeeded);

        // The at-rest value must be ciphertext, not the plaintext key.
        string? atRest = await ReadRawTotpSecretAsync(person.PersonIdentifier, cancellationToken);
        Assert.NotNull(atRest);
        Assert.NotEqual(authenticatorKey, atRest);

        Person reread = (await store.FindByIdAsync(person.PersonIdentifier.ToString(), cancellationToken))!;
        Assert.True(await store.GetTwoFactorEnabledAsync(reread, cancellationToken));
        Assert.Equal(authenticatorKey, await store.GetAuthenticatorKeyAsync(reread, cancellationToken));

        // Disabling two-factor clears the secret (== "not enrolled").
        await store.SetTwoFactorEnabledAsync(reread, false, cancellationToken);
        Assert.True((await store.UpdateAsync(reread, cancellationToken)).Succeeded);

        Person afterDisable = (await store.FindByIdAsync(person.PersonIdentifier.ToString(), cancellationToken))!;
        Assert.False(await store.GetTwoFactorEnabledAsync(afterDisable, cancellationToken));
        Assert.Null(await store.GetAuthenticatorKeyAsync(afterDisable, cancellationToken));
    }

    [Fact]
    public async Task RecoveryCodes_AreHashed_SingleUse_AndCounted()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("recovery") };
        await store.CreateAsync(person, cancellationToken);

        IReadOnlyList<string> codes = RecoveryCode.GenerateSet();
        await store.ReplaceCodesAsync(person, codes, cancellationToken);

        Assert.Equal(RecoveryCode.CodesPerSet, await store.CountCodesAsync(person, cancellationToken));

        // Stored hashed, never in plaintext.
        Assert.Equal(1, await CountRecoveryCodeRowsAsync(Sha256Hashing.Hash(codes[1]), cancellationToken));
        Assert.Equal(0, await CountRecoveryCodeRowsAsync(Encoding.UTF8.GetBytes(codes[1]), cancellationToken));

        // Redeem once; the count drops; the same code cannot be reused; an unknown code fails.
        Assert.True(await store.RedeemCodeAsync(person, codes[0], cancellationToken));
        Assert.Equal(RecoveryCode.CodesPerSet - 1, await store.CountCodesAsync(person, cancellationToken));
        Assert.False(await store.RedeemCodeAsync(person, codes[0], cancellationToken));
        Assert.False(await store.RedeemCodeAsync(person, "ZZZZZ-ZZZZZ", cancellationToken));
    }

    [Fact]
    public async Task Roles_ReadPathReflectsGrants_AndIsCaseInsensitive()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("roles") };
        await store.CreateAsync(person, cancellationToken);

        await GrantRoleDirectlyAsync(person.PersonIdentifier, "administrator", cancellationToken);
        await GrantRoleDirectlyAsync(person.PersonIdentifier, "kitchen", cancellationToken);

        IList<string> roles = await store.GetRolesAsync(person, cancellationToken);
        Assert.Equal(new[] { "administrator", "kitchen" }, roles); // ordered by role_name

        Assert.True(await store.IsInRoleAsync(person, "ADMINISTRATOR", cancellationToken)); // normalized (upper) input
        Assert.False(await store.IsInRoleAsync(person, "COUNTER", cancellationToken));

        IList<Person> kitchenStaff = await store.GetUsersInRoleAsync("KITCHEN", cancellationToken);
        Assert.Contains(kitchenStaff, candidate => candidate.PersonIdentifier == person.PersonIdentifier);
    }

    [Fact]
    public async Task EmailStore_FindByEmailIsCaseInsensitive()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("email") };
        await store.CreateAsync(person, cancellationToken);

        await store.SetEmailAsync(person, $"{person.Username}@Example.com", cancellationToken);
        Assert.True((await store.UpdateAsync(person, cancellationToken)).Succeeded);

        Person? found = await store.FindByEmailAsync($"{person.Username}@EXAMPLE.COM".ToUpperInvariant(), cancellationToken);

        Assert.NotNull(found);
        Assert.Equal(person.PersonIdentifier, found!.PersonIdentifier);
    }

    [Fact]
    public async Task DeleteAsync_IsNotSupported()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("nodelete") };
        await store.CreateAsync(person, cancellationToken);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.DeleteAsync(person, cancellationToken));
    }

    [Fact]
    public async Task AddToRoleAsync_IsNotSupported_GrantsGoThroughTheAdministrationService()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperUserStore store = BuildStore();

        Person person = new() { Username = UniqueUsername("nogrant") };
        await store.CreateAsync(person, cancellationToken);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => store.AddToRoleAsync(person, "administrator", cancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => store.RemoveFromRoleAsync(person, "administrator", cancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private DapperUserStore BuildStore() => new(
        _connectionFactory!,
        _clock,
        new UuidV7IdentifierFactory(),
        _dataProtectionProvider,
        new IdentityErrorDescriber());

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private static string UniqueUsername(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task<string?> ReadRawTotpSecretAsync(Guid personIdentifier, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT totp_secret_protected FROM person WHERE person_identifier = @Id;",
            new { Id = personIdentifier },
            cancellationToken: cancellationToken));
    }

    private async Task<int> CountRecoveryCodeRowsAsync(byte[] codeHash, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM totp_recovery_code WHERE code_hash = @CodeHash;",
            new { CodeHash = codeHash },
            cancellationToken: cancellationToken));
    }

    private async Task GrantRoleDirectlyAsync(Guid personIdentifier, string roleName, CancellationToken cancellationToken)
    {
        // Grants normally flow through the (not-yet-built) administration service; here we insert the
        // person_role row directly (self-granted) purely to set up the store's read-path tests.
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person_role (person_role_identifier, person_identifier, role_name, granted_by_person_identifier, granted_at)
            VALUES (@Id, @PersonId, @RoleName, @PersonId, @GrantedAt);
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                PersonId = personIdentifier,
                RoleName = roleName,
                GrantedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken));
    }
}
