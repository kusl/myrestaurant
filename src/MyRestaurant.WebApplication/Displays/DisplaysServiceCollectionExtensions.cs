using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// Wires the display-device services (TECHNICAL_SPECIFICATION §4.2, §11.5). Two groups:
///
/// <list type="bullet">
///   <item><description><b>Data services</b> — the read-only <see cref="IDisplayDeviceDirectory"/> the
///   administration devices page lists from, the transactional <see cref="IDisplayDevicePairing"/> it
///   issues and revokes through (and that <c>/display/pair</c> redeems through), and the
///   <see cref="IDisplayDeviceAuthenticator"/> the request middleware and the live surface re-validate
///   with. Scoped, like every other data service: they hold no state and open their own connection per
///   call from the singleton <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>.</description></item>
///   <item><description><b>The pairing rate limiter</b> — §4.2 requires <c>/display/pair</c> to be
///   "anonymous; rate-limited 5 attempts/minute/IP". Registered here as a named policy that the page
///   opts into with <c>[EnableRateLimiting]</c>; <c>Program.cs</c> adds
///   <c>UseRateLimiter()</c>.</description></item>
/// </list>
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

        services.AddRateLimiter(limiter =>
        {
            // 429 rather than the framework default of 503: the request is not being shed because the
            // server is unwell, it is being refused because this address has had its five tries.
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy<string>(DisplayRoutes.PairingRateLimitPolicy, PartitionPairingByAddress);
            limiter.OnRejected = WritePairingRejection;
        });

        return services;
    }

    /// <summary>
    /// One fixed window per client address (§4.2). The partition key is the connection's remote address,
    /// which is the real client only because <c>UseForwardedHeaders()</c> has already run — the app is
    /// always behind Caddy or the Cloudflare tunnel, so <c>UseRateLimiter()</c> must stay after it in the
    /// pipeline or every request would share the proxy's single partition.
    ///
    /// <para><c>QueueLimit = 0</c> is the point of a brute-force limit: the sixth attempt in a minute is
    /// refused immediately rather than parked until a permit frees up.</para>
    /// </summary>
    private static RateLimitPartition<string> PartitionPairingByAddress(HttpContext httpContext)
        => RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = DisplayRoutes.PairingAttemptsPerWindow,
                Window = DisplayRoutes.PairingRateLimitWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });

    /// <summary>
    /// A plain-text refusal. The status code is already set by the middleware before this runs; the body
    /// exists so a person standing at a tablet reads a sentence rather than a bare error page. No detail
    /// about the code they tried — the whole surface is an oracle-free zone (§4.2).
    /// </summary>
    private static ValueTask WritePairingRejection(OnRejectedContext context, CancellationToken cancellationToken)
    {
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        return new ValueTask(context.HttpContext.Response.WriteAsync(
            "Too many pairing attempts from this device. Wait a minute, then try again.",
            cancellationToken));
    }
}
