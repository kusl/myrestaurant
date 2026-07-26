using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// Turns a display device's credential cookie into the request's principal
/// (TECHNICAL_SPECIFICATION §4.2: "each request re-validates the hash and <c>revoked_at IS NULL</c>").
///
/// <para><b>Why middleware and not an authentication scheme.</b> The obvious .NET shape here is a custom
/// <c>AuthenticationHandler</c> registered as a second scheme. It does not work for this surface. The
/// display is an <em>interactive</em> Blazor page (§11.5 wants a live party-size chip and a
/// window-aligned refresh), and a circuit takes its principal from the <c>HttpContext</c> of the
/// <c>/_blazor</c> request that established it — which is authenticated with the <b>default</b> scheme,
/// the Identity application cookie. A device scheme would therefore populate the initial GET and then
/// hand the circuit an anonymous principal. Plain middleware runs on every request, including the
/// circuit's, so the device is present in both places. It is registered after
/// <c>UseAuthentication()</c> and before <c>UseAuthorization()</c>, exactly where a scheme's result would
/// have landed.</para>
///
/// <para><b>Why it is scoped to two path prefixes.</b> A kiosk browser is a browser: it can be walked to
/// any page in the application. Confining the credential to <see cref="DisplayRoutes.Prefix"/> and
/// <see cref="DisplayRoutes.BlazorCircuit"/> means a screen is an ordinary anonymous visitor everywhere
/// else — the shared layout never renders a device as though it were a signed-in person, and no other
/// surface has to defend against a principal whose <c>NameIdentifier</c> is not a person.</para>
///
/// <para><b>A signed-in person always wins.</b> If the Identity cookie already authenticated this
/// request, the device credential is ignored: a member of staff who opens the display URL on a paired
/// tablet is themselves, not the screen.</para>
/// </summary>
public sealed class DisplayDeviceAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DisplayDeviceAuthenticationMiddleware> _logger;

    public DisplayDeviceAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<DisplayDeviceAuthenticationMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// <see cref="IDisplayDeviceAuthenticator"/> is scoped, so it arrives per request here rather than in
    /// the constructor (middleware instances are singletons).
    /// </summary>
    public async Task InvokeAsync(HttpContext context, IDisplayDeviceAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);

        if (ShouldResolveDevice(context) && DisplayDeviceCookie.TryRead(context.Request, out DisplayDeviceCookieValue? credential)
            && credential is not null)
        {
            DisplayDeviceSession? session = await authenticator.AuthenticateAsync(
                credential.DeviceIdentifier, credential.Secret, context.RequestAborted);

            if (session is null)
            {
                // Unknown, revoked, or a secret that does not match. Clearing means the screen stops
                // presenting a credential the server will never honour again, and the pairing page can
                // greet it with §11.5's "this display was disconnected" instead of a blank form.
                DisplayDeviceCookie.Delete(context.Response);
                _logger.LogInformation(
                    "Rejected a display-device credential for {DeviceIdentifier}; the cookie was cleared.",
                    credential.DeviceIdentifier);
            }
            else
            {
                context.User = DisplayDevicePrincipal.Create(session);
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Only beneath the display prefix or on the Blazor circuit endpoint, only when nothing has already
    /// authenticated this request, and only when a cookie was actually sent — so the ordinary request
    /// pays nothing but a dictionary lookup and no database round trip happens speculatively.
    /// </summary>
    private static bool ShouldResolveDevice(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        PathString path = context.Request.Path;
        if (!path.StartsWithSegments(DisplayRoutes.Prefix) && !path.StartsWithSegments(DisplayRoutes.BlazorCircuit))
        {
            return false;
        }

        return DisplayDeviceCookie.WasPresented(context.Request);
    }
}
