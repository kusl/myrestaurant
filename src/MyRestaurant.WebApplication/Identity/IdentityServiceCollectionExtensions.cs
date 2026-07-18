using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Wires ASP.NET Core Identity <b>core</b> services (never the EF default stores/UI) over the custom
/// Dapper store, with the Argon2id hasher replacing Identity's PBKDF2 default
/// (TECHNICAL_SPECIFICATION §3.1–§3.2, ADR-0003/ADR-0008). Sign-in (<c>SignInManager</c>, cookie
/// auth, the security-stamp revalidation interval) and the passkey store are wired in a later M2
/// increment; this method only makes <see cref="UserManager{TUser}"/> resolvable and correct.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantIdentity(this IServiceCollection services, RestaurantOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

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
            .AddDefaultTokenProviders(); // Authenticator (TOTP), Data-Protection, email/phone token providers.

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

        return services;
    }
}
