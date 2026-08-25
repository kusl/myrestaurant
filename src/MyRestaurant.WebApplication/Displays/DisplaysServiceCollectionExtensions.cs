using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// Wires the display-device data services (TECHNICAL_SPECIFICATION §4.2, §11.5): the read-only
/// <see cref="IDisplayDeviceDirectory"/> the administration devices page lists from, the transactional
/// <see cref="IDisplayDevicePairing"/> it issues and revokes through (and that <c>/display/pair</c>
/// redeems through), and the <see cref="IDisplayDeviceAuthenticator"/> the request middleware and the
/// live surface re-validate with. Scoped, like every other data service: they hold no state and open
/// their own connection per call from the singleton
/// <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>.
///
/// <para><b>The pairing rate limiter used to be registered here, and moving it out was a defect fix
/// rather than a tidy-up (F-115).</b> §4.2's <em>"anonymous; rate-limited 5 attempts/minute/IP"</em> is
/// still this area's rule and <see cref="DisplayRoutes"/> still carries the budget — but
/// <c>AddRateLimiter</c> configures <c>OnRejected</c> and <c>RejectionStatusCode</c> for the
/// <em>whole limiter</em>, not for one policy, so calling it from here made a display-device extension
/// the owner of the refusal wording for every surface that might ever acquire a limit. §17 recorded that
/// as the concrete reason `/register` went eleven slices without one. The single call now lives in
/// <see cref="Security.RateLimitingServiceCollectionExtensions"/>, whose list of surfaces
/// <c>/display/pair</c> is the first entry in.</para>
///
/// <para>Kept separate from <c>AddRestaurantTables</c> because a display device is an authentication
/// concern with its own principal, not table management — though the two meet on the surface, where a
/// paired device renders the table's rotating QR through <see cref="Tables.ITableJoinTokens"/>.</para>
/// </summary>
public static class DisplaysServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantDisplays(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDisplayDeviceDirectory, DapperDisplayDeviceDirectory>();
        services.AddScoped<IDisplayDevicePairing, DapperDisplayDevicePairing>();
        services.AddScoped<IDisplayDeviceAuthenticator, DapperDisplayDeviceAuthenticator>();

        return services;
    }
}
