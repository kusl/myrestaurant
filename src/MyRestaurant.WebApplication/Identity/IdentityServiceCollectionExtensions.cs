using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Authorization;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Wires ASP.NET Core Identity <b>core</b> services (never the EF default stores/UI) over the custom
/// Dapper store, with the Argon2id hasher replacing Identity's PBKDF2 default
/// (TECHNICAL_SPECIFICATION §3.1–§3.2, ADR-0003/ADR-0008), plus sign-in and authorization:
/// <list type="bullet">
///   <item>the Identity cookie scheme, hardened (Secure, HttpOnly, SameSite=Lax, 24-hour sliding),
///   pointing at the sign-in / sign-out / access-denied surfaces in <see cref="AccountRoutes"/>;</item>
///   <item><see cref="RestaurantSignInManager"/> (the auditing <see cref="SignInManager{TUser}"/>,
///   which also refuses deactivated accounts);</item>
///   <item><see cref="RestaurantClaimsPrincipalFactory"/> — emits the role claims the area policies
///   match on (the single-generic default factory never does), plus the §3.5 obligation claims and
///   the display name;</item>
///   <item>security-stamp revalidation every 5 minutes, so resets, role revocations, and
///   deactivations bite live sessions within minutes (§3.1);</item>
///   <item>the area authorization policies (§3.7), via <see cref="AuthorizationServiceCollectionExtensions"/>;</item>
///   <item>the cascading <c>Task&lt;AuthenticationState&gt;</c> the Blazor router and
///   <c>AuthorizeView</c> consume;</item>
///   <item>the append-only <see cref="ISecurityEventLog"/> that sign-in outcomes are recorded to (§3.5);</item>
///   <item>the read-only <see cref="IPersonDirectory"/> the administration people list reads from (§3.6/§3.7).</item>
/// </list>
///
/// The obligations pipeline itself is enforced by <see cref="ObligationsMiddleware"/> in the request
/// pipeline (registered in <c>Program.cs</c>); the sign-in and forced-change pages are static-SSR
/// Razor components under <c>Components/Account</c>.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>The application authentication cookie name.</summary>
    private const string AuthenticationCookieName = "myrestaurant.authentication";

    /// <summary>Security-stamp revalidation interval (§3.1).</summary>
    private static readonly TimeSpan SecurityStampValidationInterval = TimeSpan.FromMinutes(5);

    /// <summary>Cookie sliding-expiration lifetime (§3.1).</summary>
    private static readonly TimeSpan AuthenticationCookieLifetime = TimeSpan.FromHours(24);

    public static IServiceCollection AddRestaurantIdentity(this IServiceCollection services, RestaurantOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // SignInManager and the cookie handlers resolve the current HttpContext.
        services.AddHttpContextAccessor();

        // Cookie authentication with Identity's four schemes (application, external, two-factor
        // remember-me, two-factor user-id). The application cookie is the default for
        // authenticate/challenge/forbid; the external cookie is the default sign-in scheme, matching
        // what AddIdentity configures internally (we compose it by hand to avoid a RoleManager).
        services.AddAuthentication(authentication =>
            {
                authentication.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                authentication.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                authentication.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        // Area authorization policies (§3.7).
        services.AddRestaurantAuthorization();

        // The cascading Task<AuthenticationState> that <AuthorizeView>, AuthorizeRouteView, and the
        // account pages consume — in both static SSR and interactive-server rendering.
        services.AddCascadingAuthenticationState();

        services.AddIdentityCore<Person>(identity =>
            {
                // Usernames: 3–64 chars is enforced by the DB CHECK and citext handles uniqueness, so
                // do not additionally restrict the character set (empty = "allow any"); email is optional.
                identity.User.AllowedUserNameCharacters = string.Empty;
                identity.User.RequireUniqueEmail = false;

                // Password policy (§3.2): length 12, no composition rules, no expiry.
                identity.Password.RequiredLength = 12;
                identity.Password.RequiredUniqueChars = 1;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;

                // Lockout (§3.1): 5 consecutive failures lock for 5 minutes, applied to everyone.
                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

                // No email/phone confirmation gating (optional contact fields only, §11.1).
                identity.SignIn.RequireConfirmedAccount = false;
                identity.SignIn.RequireConfirmedEmail = false;
                identity.SignIn.RequireConfirmedPhoneNumber = false;

                // No personal-data protection layer over the store (we manage TOTP encryption ourselves).
                identity.Stores.ProtectPersonalData = false;
            })
            .AddUserStore<DapperUserStore>()
            .AddClaimsPrincipalFactory<RestaurantClaimsPrincipalFactory>()
            .AddDefaultTokenProviders() // Authenticator (TOTP), Data-Protection, email/phone token providers.
            // Override the built-in authenticator provider (±2-step window) with the §3.4 ±1 one.
            // Identity's provider map keeps the last registration under a given name, so registering
            // ours under the same DefaultAuthenticatorProvider name after the defaults wins.
            .AddTokenProvider<RestaurantAuthenticatorTokenProvider>(TokenOptions.DefaultAuthenticatorProvider)
            .AddSignInManager<RestaurantSignInManager>();

        // AddIdentityCookies wires the application cookie's OnValidatePrincipal to the static
        // SecurityStampValidator, which resolves these at runtime. AddIdentityCore/AddSignInManager do
        // not register them (only the monolithic AddIdentity does), so register them explicitly.
        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<Person>>();
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<Person>>();

        // The .NET 10 WebAuthn ceremony handler (§3.3). Same story as the stamp validators: the
        // monolithic AddIdentity registers it, AddIdentityCore does not (verified against the framework
        // source), so register it explicitly or MakePasskey*OptionsAsync throws "requires an
        // IPasskeyHandler service". It reads the options configured just below.
        services.TryAddScoped<IPasskeyHandler<Person>, PasskeyHandler<Person>>();

        // Relying-party options for every passkey ceremony (§3.3). The RP ID is the FULL host of
        // RESTAURANT_PUBLIC_ORIGIN — the single origin truth of §14.2 — set explicitly so it never
        // silently falls back to the request host behind the tunnel or a hairpin. residentKey and
        // userVerification are "preferred" (discoverable + username-first both work; verification is
        // encouraged, not demanded); attestation is left at the browser default of "none", so no
        // attestation statement is requested or verified.
        services.Configure<IdentityPasskeyOptions>(passkey =>
        {
            passkey.ServerDomain = options.ResolveWebAuthnRelyingPartyId();
            passkey.UserVerificationRequirement = "preferred";
            passkey.ResidentKeyRequirement = "preferred";
        });

        // Harden the application cookie (§3.1). Secure + HttpOnly + SameSite=Lax; 24-hour sliding
        // expiration. The login/logout/access-denied paths are the real account surfaces now (this
        // slice); an unauthenticated hit on an [Authorize] page redirects to /sign-in?ReturnUrl=….
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = AuthenticationCookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.ExpireTimeSpan = AuthenticationCookieLifetime;
            cookie.SlidingExpiration = true;
            cookie.LoginPath = AccountRoutes.SignIn;
            cookie.LogoutPath = AccountRoutes.SignOut;
            cookie.AccessDeniedPath = AccountRoutes.AccessDenied;
        });

        // Revalidate the security stamp every 5 minutes so administrative resets/revocations/
        // deactivations invalidate live sessions promptly (§3.1). Rebuilding the principal also
        // refreshes the role and obligation claims through the factory above.
        services.Configure<SecurityStampValidatorOptions>(validator =>
        {
            validator.ValidationInterval = SecurityStampValidationInterval;
        });

        // Replace Identity's PBKDF2 IPasswordHasher<Person> with Argon2id (§3.2). Singleton so the
        // process-wide concurrency semaphore genuinely bounds total concurrent hashes; the duration
        // hook feeds password_hash_duration_milliseconds (§12) without DataAccess knowing about metrics.
        services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<Person>>(serviceProvider =>
        {
            RestaurantMetrics metrics = serviceProvider.GetRequiredService<RestaurantMetrics>();
            return new Argon2idPasswordHasher(
                new Argon2HashingOptions(
                    options.Argon2MemoryKibibytes,
                    options.Argon2Iterations,
                    options.Argon2Parallelism,
                    options.Argon2MaxConcurrentHashes),
                metrics.RecordPasswordHashDuration);
        }));

        // The append-only security-event trail (§3.5, §3.7). Scoped, matching the Identity lifetime;
        // it holds no state and opens a connection per write from the singleton factory.
        services.AddScoped<ISecurityEventLog, DapperSecurityEventLog>();

        // Read-only people directory for the administration area (§3.6/§3.7). Scoped, matching the
        // Identity lifetime; it holds no state and opens a connection per read from the singleton
        // factory. Its only dependency is IDatabaseConnectionFactory, so a plain type registration
        // resolves it.
        services.AddScoped<IPersonDirectory, DapperPersonDirectory>();

        // TOTP enrollment (§3.4) for the voluntary and forced pages. Scoped so it shares the request's
        // UserManager/DapperUserStore instance (it mutates the tracked entity through the store cast);
        // the factory closes over the configured RESTAURANT_NAME, which is the provisioning issuer (§13).
        services.AddScoped(serviceProvider => new TotpEnrollment(
            serviceProvider.GetRequiredService<UserManager<Person>>(),
            serviceProvider.GetRequiredService<IUserStore<Person>>(),
            serviceProvider.GetRequiredService<ISecurityEventLog>(),
            serviceProvider.GetRequiredService<IDataProtectionProvider>(),
            serviceProvider.GetRequiredService<IClock>(),
            options.RestaurantName));

        // First-administrator bootstrap (§3.6). Writes the whole first account — person, passkey, TOTP
        // secret, recovery codes, and the self-granted administrator role — in one advisory-locked
        // transaction, and answers the zero-administrator gate the /setup page and endpoint consult.
        // Scoped so it shares the request's connection-factory and data-protection lifetimes; it holds
        // no state and opens its own connection/transaction per commit from the singleton factory.
        services.AddScoped<IFirstAdministratorBootstrap>(serviceProvider => new DapperFirstAdministratorBootstrap(
            serviceProvider.GetRequiredService<IDatabaseConnectionFactory>(),
            serviceProvider.GetRequiredService<IClock>(),
            serviceProvider.GetRequiredService<IIdentifierFactory>(),
            serviceProvider.GetRequiredService<IDataProtectionProvider>()));

        return services;
    }
}
