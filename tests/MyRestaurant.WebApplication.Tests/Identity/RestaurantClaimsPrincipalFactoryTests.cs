using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

public sealed class RestaurantClaimsPrincipalFactoryTests
{
    [Fact]
    public async Task CreateAsync_EmitsOneRoleClaimPerGrantedRole_AndIsInRoleMatches()
    {
        Person user = BuildPerson();
        using UserManager<Person> userManager = BuildUserManager(user, roles: ["kitchen", "administrator"]);
        RestaurantClaimsPrincipalFactory factory = BuildFactory(userManager);

        ClaimsPrincipal principal = await factory.CreateAsync(user);

        Assert.True(principal.IsInRole("kitchen"));
        Assert.True(principal.IsInRole("administrator"));
        Assert.False(principal.IsInRole("counter"));
        Assert.Equal(2, principal.FindAll(ClaimTypes.Role).Count());
    }

    [Fact]
    public async Task CreateAsync_NameClaim_IsTheUsername()
    {
        Person user = BuildPerson(username: "casey");
        using UserManager<Person> userManager = BuildUserManager(user, roles: []);
        RestaurantClaimsPrincipalFactory factory = BuildFactory(userManager);

        ClaimsPrincipal principal = await factory.CreateAsync(user);

        Assert.Equal("casey", principal.Identity?.Name);
    }

    [Fact]
    public async Task CreateAsync_DisplayName_TravelsAsAClaimOnlyWhenPresent()
    {
        Person named = BuildPerson();
        named.DisplayName = "Casey at the counter";
        using UserManager<Person> namedManager = BuildUserManager(named, roles: []);
        ClaimsPrincipal namedPrincipal = await BuildFactory(namedManager).CreateAsync(named);

        Person unnamed = BuildPerson();
        using UserManager<Person> unnamedManager = BuildUserManager(unnamed, roles: []);
        ClaimsPrincipal unnamedPrincipal = await BuildFactory(unnamedManager).CreateAsync(unnamed);

        Assert.Equal("Casey at the counter", namedPrincipal.FindFirstValue(RestaurantClaimTypes.DisplayName));
        Assert.Null(unnamedPrincipal.FindFirst(RestaurantClaimTypes.DisplayName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CreateAsync_ObligationClaims_MirrorTheAccountFlags(bool mustChangePassword, bool mustEnrollTotp)
    {
        Person user = BuildPerson();
        user.MustChangePassword = mustChangePassword;
        user.MustEnrollTotp = mustEnrollTotp;
        using UserManager<Person> userManager = BuildUserManager(user, roles: []);
        RestaurantClaimsPrincipalFactory factory = BuildFactory(userManager);

        ClaimsPrincipal principal = await factory.CreateAsync(user);

        Assert.Equal(
            mustChangePassword ? "true" : null,
            principal.FindFirstValue(RestaurantClaimTypes.MustChangePassword));
        Assert.Equal(
            mustEnrollTotp ? "true" : null,
            principal.FindFirstValue(RestaurantClaimTypes.MustEnrollTotp));
    }

    private static Person BuildPerson(string username = "guest") => new()
    {
        PersonIdentifier = Guid.CreateVersion7(),
        Username = username,
        SecurityStamp = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static RestaurantClaimsPrincipalFactory BuildFactory(UserManager<Person> userManager)
        => new(userManager, Options.Create(new IdentityOptions()));

    private static UserManager<Person> BuildUserManager(Person user, IReadOnlyList<string> roles)
        => new(
            new FakeUserRoleStore(user, roles),
            Options.Create(new IdentityOptions()),
            passwordHasher: null!,
            userValidators: [],
            passwordValidators: [],
            keyNormalizer: null!,
            errors: new IdentityErrorDescriber(),
            services: null!,
            logger: NullLogger<UserManager<Person>>.Instance);

    private sealed class FakeUserRoleStore : IUserRoleStore<Person>
    {
        private readonly Person _user;
        private readonly IReadOnlyList<string> _roles;

        public FakeUserRoleStore(Person user, IReadOnlyList<string> roles)
        {
            _user = user;
            _roles = roles;
        }

        public Task<string> GetUserIdAsync(Person user, CancellationToken cancellationToken)
            => Task.FromResult(user.PersonIdentifier.ToString());

        public Task<string?> GetUserNameAsync(Person user, CancellationToken cancellationToken)
            => Task.FromResult<string?>(user.Username);

        public Task<IList<string>> GetRolesAsync(Person user, CancellationToken cancellationToken)
            => Task.FromResult<IList<string>>([.. _roles]);

        public Task<bool> IsInRoleAsync(Person user, string roleName, CancellationToken cancellationToken)
            => Task.FromResult(_roles.Contains(roleName, StringComparer.OrdinalIgnoreCase));

        public void Dispose()
        {
        }

        public Task SetUserNameAsync(Person user, string? userName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<string?> GetNormalizedUserNameAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task SetNormalizedUserNameAsync(Person user, string? normalizedName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<IdentityResult> CreateAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<IdentityResult> UpdateAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<IdentityResult> DeleteAsync(Person user, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<Person?> FindByIdAsync(string userId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<Person?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task AddToRoleAsync(Person user, string roleName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task RemoveFromRoleAsync(Person user, string roleName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");

        public Task<IList<Person>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by the claims factory.");
    }
}
