namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// The routes and limits of the display area (TECHNICAL_SPECIFICATION §4.2, §11.5), in one place so the
/// pages, the middleware, the rate-limiter registration, and the tests cannot drift apart — the same
/// role <see cref="Identity.AccountRoutes"/> plays for the account surfaces.
/// </summary>
public static class DisplayRoutes
{
    /// <summary>
    /// The path prefix the display-device credential is honoured on. The authentication middleware only
    /// looks at the cookie beneath this prefix (and on the Blazor circuit endpoint), so a kiosk browser
    /// that wanders to any other page is simply an anonymous visitor there.
    /// </summary>
    public const string Prefix = "/display";

    /// <summary>The anonymous, rate-limited pairing surface — code entry (§4.2).</summary>
    public const string Pair = "/display/pair";

    /// <summary>The Blazor circuit endpoint, which a paired display's interactive surface runs over.</summary>
    public const string BlazorCircuit = "/_blazor";

    /// <summary>The rate-limiter policy name applied to <see cref="Pair"/> (§4.2).</summary>
    public const string PairingRateLimitPolicy = "display-pairing";

    /// <summary>§4.2: pairing is rate-limited to 5 attempts per minute per IP address.</summary>
    public const int PairingAttemptsPerWindow = 5;

    /// <summary>The window <see cref="PairingAttemptsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan PairingRateLimitWindow = TimeSpan.FromMinutes(1);

    /// <summary>The full-screen surface for one table (§11.5).</summary>
    public static string ForTable(Guid tableIdentifier) => $"{Prefix}/{tableIdentifier:D}";
}
