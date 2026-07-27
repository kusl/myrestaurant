using MyRestaurant.Domain.Time;

namespace MyRestaurant.WebApplication.Time;

/// <summary>The route the footer clock re-anchors from. One place, so the page, the script, and the obligations exemption cannot drift apart.</summary>
public static class RestaurantClockRoutes
{
    /// <summary>
    /// <c>GET</c>, anonymous, returns a <see cref="RestaurantClockSnapshot"/> as JSON (§11.7). Anonymous
    /// because a display device, a signed-out sign-in page, and a guest mid-order all carry the same
    /// footer and all need the same answer; the response contains nothing that is not already printed
    /// on the page that asked for it.
    /// </summary>
    public const string Snapshot = "/restaurant-clock";
}

/// <summary>
/// Serves the footer clock's anchor (TECHNICAL_SPECIFICATION §11.7).
///
/// <para><b>Why an endpoint at all.</b> The page already ships an anchor in its markup, and for a
/// short-lived page that is the whole story. But two surfaces here are not short-lived: a table display
/// is a tablet that stays on the same URL for days (§11.5), and a guest's ordering surface holds one
/// circuit for the length of a meal. A clock anchored once and then driven by a cheap tablet's
/// oscillator drifts; a phone that suspends may stop advancing <c>performance.now()</c> while it sleeps;
/// and a page open across a daylight-saving boundary needs the new offset. Rather than reload the page
/// for any of that, <c>js/clock.js</c> re-asks here — rarely, and never while the tab is hidden.</para>
/// </summary>
public static class RestaurantClockEndpoints
{
    public static IEndpointRouteBuilder MapRestaurantClock(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            RestaurantClockRoutes.Snapshot,
            (RestaurantTime restaurantTime, IClock clock, HttpContext context) =>
            {
                // A cached wall clock is a wrong wall clock. Also keeps a shared proxy from handing one
                // table's anchor to another table twenty minutes later.
                context.Response.Headers.CacheControl = "no-store";

                return Results.Json(restaurantTime.Snapshot(clock.UtcNow));
            })
            .AllowAnonymous()
            .WithName("RestaurantClockSnapshot");

        return endpoints;
    }
}
