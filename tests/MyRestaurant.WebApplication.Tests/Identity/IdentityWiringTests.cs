using System.Data.Common;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Authorization;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.Observability;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the sign-in and authorization wiring composed by
/// <see cref="IdentityServiceCollectionExtensions.AddRestaurantIdentity"/>
/// (TECHNICAL_SPECIFICATION §3.1, §3.5, §3.7): the auditing <see cref="RestaurantSignInManager"/> is
/// the resolved <see cref="SignInManager{TUser}"/>; the claims principal factory is the restaurant
/// one (role + obligation claims — the default single-generic factory emits no role claims at all,
/// so this registration is what makes the area policies passable); the application cookie is
/// hardened (Secure, HttpOnly, SameSite=Lax, 24-hour sliding) and points at the account routes; the
/// security stamp revalidates every 5 minutes; and the four area policies require the right roles.
/// No server is started — the container is built and inspected.
/// </summary>
public sealed class IdentityWiringTests
{
    [Fact]
    public void SignInManager_IsTheAuditingRestaurantSignInManager()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        SignInManager<Person> signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<Person>>();

        Assert.IsType<RestaurantSignInManager>(signInManager);
    }

    [Fact]
    public void ClaimsPrincipalFactory_IsTheRestaurantFactory()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        IUserClaimsPrincipalFactory<Person> factory =
            scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<Person>>();

        Assert.IsType<RestaurantClaimsPrincipalFactory>(factory);
    }

    [Fact]
    public void ApplicationCookie_IsHardenedPerSpec()
    {
        using ServiceProvider provider = BuildProvider();

        CookieAuthenticationOptions cookie = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromHours(24), cookie.ExpireTimeSpan);
        Assert.True(cookie.SlidingExpiration);
    }

    [Fact]
    public void ApplicationCookie_PointsAtTheAccountRoutes()
    {
        using ServiceProvider provider = BuildProvider();

        CookieAuthenticationOptions cookie = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        Assert.Equal(AccountRoutes.SignIn, cookie.LoginPath);
        Assert.Equal(AccountRoutes.SignOut, cookie.LogoutPath);
        Assert.Equal(AccountRoutes.AccessDenied, cookie.AccessDeniedPath);
    }

    [Fact]
    public void SecurityStamp_RevalidatesEveryFiveMinutes()
    {
        using ServiceProvider provider = BuildProvider();

        SecurityStampValidatorOptions options = provider
            .GetRequiredService<IOptions<SecurityStampValidatorOptions>>()
            .Value;

        Assert.Equal(TimeSpan.FromMinutes(5), options.ValidationInterval);
    }

    [Fact]
    public void AuthenticatorTokenProvider_IsTheRestaurantOnePointOneStepOverride()
    {
        // §3.4 requires a ±1 window; the framework default is ±2. Registering our provider under the
        // same DefaultAuthenticatorProvider name after AddDefaultTokenProviders must win the map.
        using ServiceProvider provider = BuildProvider();

        IdentityOptions options = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.True(options.Tokens.ProviderMap.TryGetValue(TokenOptions.DefaultAuthenticatorProvider, out var descriptor));
        Assert.Equal(typeof(RestaurantAuthenticatorTokenProvider), descriptor!.ProviderType);
    }

    [Fact]
    public void TotpEnrollment_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        TotpEnrollment enrollment = scope.ServiceProvider.GetRequiredService<TotpEnrollment>();

        Assert.NotNull(enrollment);
    }

    [Fact]
    public void PasskeyHandler_IsRegistered()
    {
        // AddIdentityCore does not register IPasskeyHandler (only the monolithic AddIdentity does), so
        // AddRestaurantIdentity must — otherwise MakePasskey*OptionsAsync throws at runtime (§3.3).
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        IPasskeyHandler<Person> handler = scope.ServiceProvider.GetRequiredService<IPasskeyHandler<Person>>();

        Assert.IsType<PasskeyHandler<Person>>(handler);
    }

    [Fact]
    public void UserManager_SupportsPasskeys()
    {
        // The Dapper store now implements IUserPasskeyStore, which is how UserManager exposes the
        // passkey capability (it casts its store).
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        UserManager<Person> userManager = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();

        Assert.True(userManager.SupportsUserPasskey);
    }

    [Fact]
    public void PasskeyOptions_UseTheConfiguredRelyingPartyAndPreferredVerification()
    {
        // §3.3: RP ID is the host of RESTAURANT_PUBLIC_ORIGIN (localhost for the test options), and
        // residentKey + userVerification are both "preferred".
        using ServiceProvider provider = BuildProvider();

        IdentityPasskeyOptions options = provider.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value;

        Assert.Equal("localhost", options.ServerDomain);
        Assert.Equal("preferred", options.UserVerificationRequirement);
        Assert.Equal("preferred", options.ResidentKeyRequirement);
    }

    [Fact]
    public async Task TablePolicy_RequiresOnlyAuthentication()
    {
        AuthorizationPolicy policy = await GetPolicyAsync(AuthorizationPolicies.Table);

        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        Assert.DoesNotContain(policy.Requirements, requirement => requirement is RolesAuthorizationRequirement);
    }

    [Theory]
    [InlineData(AuthorizationPolicies.Kitchen, RestaurantRoles.Kitchen, RestaurantRoles.Administrator)]
    [InlineData(AuthorizationPolicies.Counter, RestaurantRoles.Counter, RestaurantRoles.Administrator)]
    public async Task StaffPolicies_AllowTheRoleOrAdministrator(string policyName, string role, string administrator)
    {
        AuthorizationPolicy policy = await GetPolicyAsync(policyName);

        RolesAuthorizationRequirement roles = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(
            new[] { administrator, role }.OrderBy(r => r),
            roles.AllowedRoles.OrderBy(r => r));
        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task AdministrationPolicy_RequiresTheAdministratorRole()
    {
        AuthorizationPolicy policy = await GetPolicyAsync(AuthorizationPolicies.Administration);

        RolesAuthorizationRequirement roles = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(new[] { RestaurantRoles.Administrator }, roles.AllowedRoles);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static async Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
    {
        using ServiceProvider provider = BuildProvider();
        IAuthorizationPolicyProvider policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        AuthorizationPolicy? policy = await policyProvider.GetPolicyAsync(policyName);

        Assert.NotNull(policy);
        return policy!;
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // Prerequisites Program.cs registers before AddRestaurantIdentity.
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddDataProtection();

        services.AddRestaurantIdentity(BuildOptions());

        return services.BuildServiceProvider();
    }

    private static RestaurantOptions BuildOptions() => new()
    {
        RestaurantName = "Test Bistro",
        PublicOrigin = "https://localhost:8443",
        TimeZoneId = "America/New_York",
        CurrencyCode = "USD",
        DatabaseConnectionString = "Host=localhost;Database=x;Username=u;Password=p",
        DataProtectionKeysDirectory = "/tmp/myrestaurant-keys",
        KitchenSubmissionReminderSeconds = 60,
        TableJoinTokenRotationSeconds = 60,
        TableJoinGrantMinutes = 10,
        TableDisplayPairingCodeMinutes = 10,
        Argon2MemoryKibibytes = 65536,
        Argon2Iterations = 3,
        Argon2Parallelism = 1,
        Argon2MaxConcurrentHashes = 4,
    };

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
