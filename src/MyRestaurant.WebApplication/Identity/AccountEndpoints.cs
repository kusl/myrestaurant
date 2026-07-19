using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MyRestaurant.DataAccess.Identity;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The non-page account endpoints. Sign-out is a POST endpoint rather than a page
/// (TECHNICAL_SPECIFICATION §3.5): it must clear the authentication cookies on the response, must
/// never be triggerable by a crafted GET link, and must stay reachable while the obligations
/// pipeline blocks everything else (its path is exempt in <see cref="ObligationsEnforcement"/>).
/// Binding the optional form field makes the endpoint "accept form data", which switches on the
/// framework's automatic antiforgery validation for it — the layout's sign-out form supplies the
/// token via <c>&lt;AntiforgeryToken /&gt;</c>.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapRestaurantAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(AccountRoutes.SignOut, async (
            SignInManager<Person> signInManager,
            [FromForm] string? returnUrl) =>
        {
            // Clears the application, external, and both two-factor cookies. Harmless when the
            // caller was already anonymous. No security_event is written: sign-out is not in the
            // §8.2 event-type vocabulary (sessions also end silently by expiry and stamp rotation,
            // so recording only explicit sign-outs would tell a misleading story).
            await signInManager.SignOutAsync();
            return Results.LocalRedirect(ObligationsEnforcement.SafeLocalReturnUrl(returnUrl));
        });

        return endpoints;
    }
}
