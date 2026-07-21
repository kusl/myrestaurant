using Microsoft.AspNetCore.Antiforgery;
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
///
/// <para>The two passkey-options endpoints (§3.3) return the WebAuthn ceremony JSON that the browser
/// feeds to <c>navigator.credentials</c>. They are fetched by the <c>passkey-submit</c> element, not
/// posted by a form, so they validate the antiforgery token manually from the request header (the
/// element carries it) rather than via form binding. Creation options require an authenticated user
/// (you register a passkey for yourself); request options are anonymous, because sign-in — including
/// the username-first and discoverable flows — happens before there is a session.</para>
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

        // Registration ceremony options for the signed-in user (attestation). The user entity's Id is
        // the WebAuthn user handle; the framework stashes the challenge in a short-lived cookie that
        // PerformPasskeyAttestationAsync later reads.
        endpoints.MapPost(AccountRoutes.PasskeyCreationOptions, async (
            HttpContext context,
            UserManager<Person> userManager,
            SignInManager<Person> signInManager,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            Person? user = await userManager.GetUserAsync(context.User);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            string userId = await userManager.GetUserIdAsync(user);
            string userName = await userManager.GetUserNameAsync(user) ?? user.Username;
            string optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = userId,
                Name = userName,
                DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? userName : user.DisplayName,
            });

            return Results.Content(optionsJson, "application/json");
        }).RequireAuthorization();

        // Assertion ceremony options for sign-in (anonymous). With a username we scope allowCredentials
        // to that account (username-first); without one we return no allowCredentials so the browser
        // offers any discoverable passkey for this relying party.
        endpoints.MapPost(AccountRoutes.PasskeyRequestOptions, async (
            HttpContext context,
            UserManager<Person> userManager,
            SignInManager<Person> signInManager,
            IAntiforgery antiforgery,
            [FromQuery] string? username) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            Person? user = string.IsNullOrWhiteSpace(username)
                ? null
                : await userManager.FindByNameAsync(username);
            string optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user);

            return Results.Content(optionsJson, "application/json");
        });

        return endpoints;
    }
}
