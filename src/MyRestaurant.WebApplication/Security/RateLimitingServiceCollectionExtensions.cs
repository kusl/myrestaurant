using Microsoft.AspNetCore.RateLimiting;

namespace MyRestaurant.WebApplication.Security;

/// <summary>
/// The one <c>AddRateLimiter</c> call in this application (TECHNICAL_SPECIFICATION §4.2, §11.8, §17,
/// F-115).
///
/// <para><b>One call, and the singular is the whole design.</b> <c>RateLimiterOptions.OnRejected</c> and
/// <c>RejectionStatusCode</c> are properties of the limiter rather than of a policy, so a second
/// <c>AddRateLimiter</c> anywhere in the composition root silently takes the refusal wording away from
/// the first — no error, no warning, and a surface that answers in another surface's words. §17 recorded
/// that as the concrete reason `/register` had no limit. It is discharged by owning the call here and
/// by <see cref="RateLimitedSurfaces.All"/> being the only thing it reads: a new limited surface is a
/// new entry in that list, and it cannot be added without a refusal sentence of its own.</para>
///
/// <para><b>Placed in <c>Security</c> rather than beside a surface.</b> That is the correction, not
/// tidying. The limiter lived in <c>AddRestaurantDisplays</c> because pairing was the only thing limited
/// — which made a display-device extension the owner of the rejection handler for every future policy,
/// and made §17's wall invisible to anybody reading either surface. A cross-cutting response concern
/// belongs with <c>SecurityHeadersMiddleware</c>, which is the same shape: something every response may
/// be shaped by, owned in one place, and asserted rather than remembered.</para>
///
/// <para><b><c>app.UseRateLimiter()</c> is still the caller's job</b> and its position is still
/// load-bearing: it must sit after <c>UseForwardedHeaders()</c>, because both partitioners key on the
/// connection's remote address and before the forwarded headers are applied that address is the
/// proxy's — one partition for the whole building. <c>Program.cs</c> says so at the line.</para>
/// </summary>
public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(limiter =>
        {
            // 429 rather than the framework default of 503, for both surfaces: the request is not being
            // shed because the server is unwell, it is being refused because this address has spent its
            // budget. A 503 additionally invites a retry-after-immediately client and, on the pairing
            // surface, tells an installer to go and look at the server.
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRefusal;

            foreach (RateLimitedSurface surface in RateLimitedSurfaces.All)
            {
                limiter.AddPolicy<string>(surface.PolicyName, surface.Partition);
            }
        });

        return services;
    }

    /// <summary>
    /// The refusal body, dispatched on the endpoint that was refused (§17's stated fix).
    ///
    /// <para><b>The lookup is the middleware's own.</b> The rate-limiting middleware selected the policy
    /// for this request by reading <c>EnableRateLimitingAttribute</c> out of the endpoint's metadata; the
    /// same read here returns the same name. So this is not a heuristic recovering information the
    /// framework lost — it is asking the same question of the same object one instant later, which is why
    /// a mismatch is not a case that needs handling so much as a case that cannot arise.</para>
    ///
    /// <para>The status code is already on the response before this runs, set from
    /// <c>RejectionStatusCode</c>. The body exists so that a person standing at a tablet or holding a
    /// phone at a table reads a sentence rather than a bare error page — and, since both limited surfaces
    /// are anonymous, it says nothing about what was attempted (§4.2's oracle-free rule, which §11.8
    /// inherits by being anonymous and writing).</para>
    /// </summary>
    private static ValueTask WriteRefusal(OnRejectedContext context, CancellationToken cancellationToken)
    {
        string? policyName = context.HttpContext
            .GetEndpoint()?
            .Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?
            .PolicyName;

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";

        return new ValueTask(context.HttpContext.Response.WriteAsync(
            RateLimitedSurfaces.RefusalFor(policyName),
            cancellationToken));
    }
}
