using Microsoft.AspNetCore.RateLimiting;

namespace MyRestaurant.WebApplication.Security;

public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRefusal;

            foreach (RateLimitedSurface surface in RateLimitedSurfaces.All)
            {
                limiter.AddPolicy<string>(surface.PolicyName, surface.Partition);
            }
        });

        return services;
    }

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
