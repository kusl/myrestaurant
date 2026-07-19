using System.Security.Claims;
using MyRestaurant.Domain.Authentication;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Enforces the post-authentication obligations pipeline (TECHNICAL_SPECIFICATION §3.5, ADR-0010):
/// once a principal is authenticated, an outstanding <c>must_change_password</c> /
/// <c>must_enroll_totp</c> flag makes every endpoint unreachable except sign-out and the pipeline's
/// own pages — the request is redirected to the page that clears the next obligation, carrying the
/// original destination as <c>ReturnUrl</c>.
///
/// <para>The <em>decision</em> is the pure <see cref="ObligationsPipeline"/>; the claim mapping,
/// exemption list, and redirect construction are the testable statics on
/// <see cref="ObligationsEnforcement"/>; this class is only the pipeline shell. It sits after
/// authentication (it reads <see cref="HttpContext.User"/>) and before the endpoint runs. Anonymous
/// requests pass through untouched — the flags only exist on authenticated principals.</para>
///
/// <para>Two deliberate consequences: static files served by <c>UseStaticFiles</c> never reach this
/// middleware (it sits after them), and the Blazor circuit endpoint is <b>not</b> exempt — an
/// interactive tab left open when a flag lands cannot reconnect its circuit until the pipeline
/// clears, which is exactly the "nothing else reachable" the specification asks for.</para>
/// </summary>
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
