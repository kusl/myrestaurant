namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// The routes and limits of the display area (TECHNICAL_SPECIFICATION §4.2, §11.5), in one place so the
/// pages, the middleware, the rate-limiter partitioner, and the tests cannot drift apart.
///
/// <para>The pairing <em>policy name</em> is not here; it moved to
/// <see cref="Security.RateLimitedSurfaces"/> in Slice 62 when a second surface acquired a limit
/// (F-115). The pairing <em>budget</em> below stayed, because it is §4.2's number about this area
/// rather than a key shared with another one.</para>
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

    /// <summary>
    /// §4.2: pairing is rate-limited to 5 attempts per minute per IP address.
    ///
    /// <para>The <em>budget</em> is here and the <em>policy name</em> is not, and the line between them
    /// is deliberate (F-115). This number is §4.2's, stated normatively about this surface, and there is
    /// no operator decision in it — a member of staff installs a tablet, once. The policy name is the key
    /// three unrelated readers agree on (the page's attribute, the limiter registration, and the refusal
    /// dispatch), and once a second surface acquired a limit the only honest home for either name was the
    /// list of them: <see cref="Security.RateLimitedSurfaces.PairingPolicy"/>.</para>
    /// </summary>
    public const int PairingAttemptsPerWindow = 5;

    /// <summary>The window <see cref="PairingAttemptsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan PairingRateLimitWindow = TimeSpan.FromMinutes(1);

    /// <summary>The full-screen surface for one table (§11.5).</summary>
    public static string ForTable(Guid tableIdentifier) => $"{Prefix}/{tableIdentifier:D}";
}
