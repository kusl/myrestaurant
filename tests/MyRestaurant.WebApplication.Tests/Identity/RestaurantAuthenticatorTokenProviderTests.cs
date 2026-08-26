using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

public sealed class RestaurantAuthenticatorTokenProviderTests
{
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private static readonly DateTimeOffset Anchor = DateTimeOffset.FromUnixTimeSeconds(1111111109);

    [Theory]
    [InlineData("081804")]
    [InlineData("731029")]
    [InlineData("050471")]
    public async Task ValidateAsync_AcceptsCodesWithinOneStep(string code)
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(RfcSecretBase32);
        using (manager)
        {
            Assert.True(await provider.ValidateAsync("TwoFactor", code, manager, user));
        }
    }

    [Theory]
    [InlineData("150727")]
    [InlineData("266759")]
    public async Task ValidateAsync_RejectsCodesTwoStepsAway(string code)
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(RfcSecretBase32);
        using (manager)
        {
            Assert.False(await provider.ValidateAsync("TwoFactor", code, manager, user));
        }
    }

    [Theory]
    [InlineData("050 471")]
    [InlineData("050-471")]
    public async Task ValidateAsync_ToleratesGroupedDisplaySeparators(string code)
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(RfcSecretBase32);
        using (manager)
        {
            Assert.True(await provider.ValidateAsync("TwoFactor", code, manager, user));
        }
    }

    [Fact]
    public async Task ValidateAsync_WrongCode_IsRejected()
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(RfcSecretBase32);
        using (manager)
        {
            Assert.False(await provider.ValidateAsync("TwoFactor", "000000", manager, user));
        }
    }

    [Theory]
    [InlineData("81804")]
    [InlineData("08180a")]
    [InlineData("")]
    public async Task ValidateAsync_MalformedInput_IsRejected(string code)
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(RfcSecretBase32);
        using (manager)
        {
            Assert.False(await provider.ValidateAsync("TwoFactor", code, manager, user));
        }
    }

    [Fact]
    public async Task ValidateAsync_NoKey_IsRejected()
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(key: null);
        using (manager)
        {
            Assert.False(await provider.ValidateAsync("TwoFactor", "081804", manager, user));
        }
    }

    [Fact]
    public async Task CanGenerateTwoFactorTokenAsync_TracksWhetherAKeyIsPresent()
    {
        (RestaurantAuthenticatorTokenProvider withKey, UserManager<Person> withKeyManager, Person a) = Build(RfcSecretBase32);
        using (withKeyManager)
        {
            Assert.True(await withKey.CanGenerateTwoFactorTokenAsync(withKeyManager, a));
        }

        (RestaurantAuthenticatorTokenProvider noKey, UserManager<Person> noKeyManager, Person b) = Build(key: null);
        using (noKeyManager)
        {
            Assert.False(await noKey.CanGenerateTwoFactorTokenAsync(noKeyManager, b));
        }
    }

    [Fact]
    public async Task GenerateAsync_ReturnsEmpty()
    {
        (RestaurantAuthenticatorTokenProvider provider, UserManager<Person> manager, Person user) = Build(RfcSecretBase32);
        using (manager)
        {
            Assert.Equal(string.Empty, await provider.GenerateAsync("TwoFactor", manager, user));
        }
    }

    private static (RestaurantAuthenticatorTokenProvider, UserManager<Person>, Person) Build(string? key)
    {
        Person user = new()
        {
            PersonIdentifier = Guid.CreateVersion7(),
            Username = "casey",
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = Anchor,
        };

        UserManager<Person> manager = new(
            new FakeAuthenticatorKeyStore(user, key),
            Options.Create(new IdentityOptions()),
            passwordHasher: null!,
            userValidators: [],
            passwordValidators: [],
            keyNormalizer: null!,
            errors: new IdentityErrorDescriber(),
            services: null!,
            logger: NullLogger<UserManager<Person>>.Instance);

        RestaurantAuthenticatorTokenProvider provider = new(new FixedClock(Anchor));
        return (provider, manager, user);
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FakeAuthenticatorKeyStore : IUserAuthenticatorKeyStore<Person>
    {
        private readonly Person _user;
        private readonly string? _key;

        public FakeAuthenticatorKeyStore(Person user, string? key)
        {
            _user = user;
            _key = key;
        }

        public Task<string?> GetAuthenticatorKeyAsync(Person user, CancellationToken cancellationToken)
            => Task.FromResult(_key);

        public Task SetAuthenticatorKeyAsync(Person user, string key, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<string> GetUserIdAsync(Person user, CancellationToken cancellationToken)
            => Task.FromResult(user.PersonIdentifier.ToString());

        public Task<string?> GetUserNameAsync(Person user, CancellationToken cancellationToken)
            => Task.FromResult<string?>(user.Username);

        public Task SetUserNameAsync(Person user, string? userName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<string?> GetNormalizedUserNameAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task SetNormalizedUserNameAsync(Person user, string? normalizedName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<IdentityResult> CreateAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<IdentityResult> UpdateAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<IdentityResult> DeleteAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<Person?> FindByIdAsync(string userId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public Task<Person?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the token provider.");

        public void Dispose()
        {
        }
    }
}
