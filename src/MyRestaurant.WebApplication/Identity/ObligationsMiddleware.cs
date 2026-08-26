using System.Security.Claims;
using MyRestaurant.Domain.Authentication;

namespace MyRestaurant.WebApplication.Identity;

public sealed class ObligationsMiddleware
{
    private readonly RequestDelegate _next;

    public ObligationsMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ClaimsPrincipal user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            PostAuthenticationObligation obligation = ObligationsEnforcement.NextObligationFor(user);
            if (obligation != PostAuthenticationObligation.None
                && !ObligationsEnforcement.IsExemptPath(context.Request.Path))
            {
                context.Response.Redirect(ObligationsEnforcement.RedirectTargetFor(obligation, context.Request));
                return Task.CompletedTask;
            }
        }

        return _next(context);
    }
}
