using MyRestaurant.Domain.Time;

namespace MyRestaurant.WebApplication.Time;

public static class RestaurantClockRoutes
{
    public const string Snapshot = "/restaurant-clock";
}

public static class RestaurantClockEndpoints
{
    public static IEndpointRouteBuilder MapRestaurantClock(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            RestaurantClockRoutes.Snapshot,
            (RestaurantTime restaurantTime, IClock clock, HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";

                return Results.Json(restaurantTime.Snapshot(clock.UtcNow));
            })
            .AllowAnonymous()
            .WithName("RestaurantClockSnapshot");

        return endpoints;
    }
}
