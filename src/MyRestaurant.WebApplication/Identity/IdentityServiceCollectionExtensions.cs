using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.WebApplication.Authorization;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Wires ASP.NET Core Identity <b>core</b> services (never the EF default stores/UI) over the custom
/// Dapper store, with the Argon2id hasher replacing Identity's PBKDF2 default
/// (TECHNICAL_SPECIFICATION §3.1–§3.2, ADR-0003/ADR-0008), plus sign-in and authorization:
/// <list type="bullet">
///   <item>the Identity cookie scheme, hardened (Secure, HttpOnly, SameSite=Lax, 24-hour sliding);</item>
///   <item><see cref="RestaurantSignInManager"/> (the auditing <see cref="SignInManager{TUser}"/>);</item>
///   <item>security-stamp revalidation every 5 minutes, so resets, role revocations, and
///   deactivations bite live sessions within minutes (§3.1);</item>
///   <item>the area authorization policies (§3.7), via <see cref="AuthorizationServiceCollectionExtensions"/>;</item>
///   <item>the append-only <see cref="ISecurityEventLog"/> that sign-in outcomes are recorded to (§3.5).</item>
/// </list>
///
/// Roles flow to claims automatically: the store implements <see cref="IUserRoleStore{TUser}"/>, so the
/// default claims-principal factory adds a role claim per granted role at sign-in — no role entity or
/// <c>RoleManager</c> is registered (roles are plain strings, §3.7). The sign-in <em>pages</em> and the
/// obligations-pipeline middleware are the next M2 slices; this method wires the services they need.
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
            .AddDefaultTokenProviders() // Authenticator (TOTP), Data-Protection, email/phone token providers.
            .AddSignInManager<RestaurantSignInManager>();

        // AddIdentityCookies wires the application cookie's OnValidatePrincipal to the static
        // SecurityStampValidator, which resolves these at runtime. AddIdentityCore/AddSignInManager do
        // not register them (only the monolithic AddIdentity does), so register them explicitly.
        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<Person>>();
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<Person>>();

        // Harden the application cookie (§3.1). Secure + HttpOnly + SameSite=Lax; 24-hour sliding
        // expiration. The paths point at the sign-in surfaces built in the next slice; nothing is
        // authorized yet, so no redirect fires until those pages and their [Authorize] attributes exist.
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = AuthenticationCookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.ExpireTimeSpan = AuthenticationCookieLifetime;
            cookie.SlidingExpiration = true;
            cookie.LoginPath = "/sign-in";
            cookie.LogoutPath = "/sign-out";
            cookie.AccessDeniedPath = "/access-denied";
        });

        // Revalidate the security stamp every 5 minutes so administrative resets/revocations/
        // deactivations invalidate live sessions promptly (§3.1).
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

        return services;
    }
}
