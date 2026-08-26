using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

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
