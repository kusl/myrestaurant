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

public static class IdentityServiceCollectionExtensions
{
    private const string AuthenticationCookieName = "myrestaurant.authentication";

    private static readonly TimeSpan SecurityStampValidationInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan AuthenticationCookieLifetime = TimeSpan.FromHours(24);

    public static IServiceCollection AddRestaurantIdentity(this IServiceCollection services, RestaurantOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHttpContextAccessor();

        services.AddAuthentication(authentication =>
            {
                authentication.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                authentication.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                authentication.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services.AddRestaurantAuthorization();

        services.AddCascadingAuthenticationState();

        services.AddIdentityCore<Person>(identity =>
            {
                identity.User.AllowedUserNameCharacters = string.Empty;
                identity.User.RequireUniqueEmail = false;

                identity.Password.RequiredLength = 12;
                identity.Password.RequiredUniqueChars = 1;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;

                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

                identity.SignIn.RequireConfirmedAccount = false;
                identity.SignIn.RequireConfirmedEmail = false;
                identity.SignIn.RequireConfirmedPhoneNumber = false;

                identity.Stores.ProtectPersonalData = false;
            })
            .AddUserStore<DapperUserStore>()
            .AddClaimsPrincipalFactory<RestaurantClaimsPrincipalFactory>()
            .AddDefaultTokenProviders()

            .AddTokenProvider<RestaurantAuthenticatorTokenProvider>(TokenOptions.DefaultAuthenticatorProvider)
            .AddSignInManager<RestaurantSignInManager>();

        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<Person>>();
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<Person>>();

        services.TryAddScoped<IPasskeyHandler<Person>, PasskeyHandler<Person>>();

        WebAuthnOriginPolicy originPolicy = new(options.PublicOrigin, options.TrustedOriginPatterns);
        services.AddSingleton(originPolicy);

        services.Configure<IdentityPasskeyOptions>(passkey =>
        {
            passkey.ServerDomain = null;
            passkey.UserVerificationRequirement = "preferred";
            passkey.ResidentKeyRequirement = "preferred";
            passkey.ValidateOrigin = context =>
                ValueTask.FromResult(!context.CrossOrigin && originPolicy.IsTrustedOrigin(context.Origin));
        });

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

        services.Configure<SecurityStampValidatorOptions>(validator =>
        {
            validator.ValidationInterval = SecurityStampValidationInterval;
        });

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

        services.AddScoped<ISecurityEventLog, DapperSecurityEventLog>();

        services.AddScoped<IPersonDirectory, DapperPersonDirectory>();

        services.AddScoped(serviceProvider => new TotpEnrollment(
            serviceProvider.GetRequiredService<UserManager<Person>>(),
            serviceProvider.GetRequiredService<IUserStore<Person>>(),
            serviceProvider.GetRequiredService<ISecurityEventLog>(),
            serviceProvider.GetRequiredService<IDataProtectionProvider>(),
            serviceProvider.GetRequiredService<IClock>(),
            options.RestaurantName));

        services.AddScoped<IFirstAdministratorBootstrap>(serviceProvider => new DapperFirstAdministratorBootstrap(
            serviceProvider.GetRequiredService<IDatabaseConnectionFactory>(),
            serviceProvider.GetRequiredService<IClock>(),
            serviceProvider.GetRequiredService<IIdentifierFactory>(),
            serviceProvider.GetRequiredService<IDataProtectionProvider>()));

        services.AddScoped<IAccountAdministration>(serviceProvider => new DapperAccountAdministration(
            serviceProvider.GetRequiredService<IDatabaseConnectionFactory>(),
            serviceProvider.GetRequiredService<IClock>(),
            serviceProvider.GetRequiredService<IIdentifierFactory>()));

        services.AddScoped<IGuestRegistration>(serviceProvider => new DapperGuestRegistration(
            serviceProvider.GetRequiredService<IDatabaseConnectionFactory>(),
            serviceProvider.GetRequiredService<IClock>(),
            serviceProvider.GetRequiredService<IIdentifierFactory>()));

        return services;
    }
}
