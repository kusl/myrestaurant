namespace MyRestaurant.WebApplication.Configuration;

/// <summary>
/// The route the source offer lives on (TECHNICAL_SPECIFICATION §11.9). One constant, so the page,
/// the footer colophon that links to it, and the §3.5 obligations exemption that keeps it reachable
/// cannot drift apart — the same reason <c>RestaurantClockRoutes</c> and <c>AccountRoutes</c> exist.
/// </summary>
public static class SourceRoutes
{
    /// <summary>
    /// The source offer (AGPL-3.0-only §13). Anonymous and static SSR: it must answer for a guest who
    /// has never signed in, for a tablet with no session, and for a person the obligations pipeline
    /// has otherwise locked down — a licence offer that only signed-in users can read is not an offer
    /// to "all users interacting with it remotely".
    /// </summary>
    public const string Source = "/source";
}
