using System.Threading.RateLimiting;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;

namespace MyRestaurant.WebApplication.Security;

public sealed class RateLimitedSurface
{
    public required string PolicyName { get; init; }

    public required string Refusal { get; init; }

    public required Func<HttpContext, RateLimitPartition<string>> Partition { get; init; }
}

public static class RateLimitedSurfaces
{
    public const string PairingPolicy = "display-pairing";

    public const string GuestRegistrationPolicy = "guest-registration";

    public static readonly string GenericRefusal =
        "Too many attempts from this device. Wait a few minutes, then try again.";

    public static IReadOnlyList<RateLimitedSurface> All { get; } =
    [
        new RateLimitedSurface
        {
            PolicyName = PairingPolicy,
            Refusal = "Too many pairing attempts from this device. Wait a minute, then try again.",
            Partition = PartitionPairingByAddress,
        },
        new RateLimitedSurface
        {
            PolicyName = GuestRegistrationPolicy,
            Refusal = "Too many accounts have been created from this network. Wait a few minutes, then"
                + " try again — or ask a member of staff.",
            Partition = PartitionGuestRegistrationByAddress,
        },
    ];

    public static string RefusalFor(string? policyName)
    {
        if (string.IsNullOrEmpty(policyName))
        {
            return GenericRefusal;
        }

        foreach (RateLimitedSurface surface in All)
        {
            if (string.Equals(surface.PolicyName, policyName, StringComparison.Ordinal))
            {
                return surface.Refusal;
            }
        }

        return GenericRefusal;
    }

    private static RateLimitPartition<string> PartitionPairingByAddress(HttpContext httpContext)
        => RateLimitPartition.GetFixedWindowLimiter(
            PartitionKeyFor(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = DisplayRoutes.PairingAttemptsPerWindow,
                Window = DisplayRoutes.PairingRateLimitWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });

    private static RateLimitPartition<string> PartitionGuestRegistrationByAddress(HttpContext httpContext)
    {
        RestaurantOptions options = httpContext.RequestServices.GetRequiredService<RestaurantOptions>();

        return RateLimitPartition.GetFixedWindowLimiter(
            PartitionKeyFor(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.GuestRegistrationAttemptsPerWindow,
                Window = TimeSpan.FromMinutes(options.GuestRegistrationWindowMinutes),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }

    private static string PartitionKeyFor(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
