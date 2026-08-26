using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.WebApplication.Identity;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapRestaurantAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(AccountRoutes.SignOut, async (
            SignInManager<Person> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect(ObligationsEnforcement.SafeLocalReturnUrl(returnUrl));
        });

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

        endpoints.MapPost(AccountRoutes.SetupPasskeyCreationOptions, async (
            HttpContext context,
            IFirstAdministratorBootstrap bootstrap,
            SignInManager<Person> signInManager,
            IAntiforgery antiforgery,
            IDataProtectionProvider dataProtectionProvider,
            IClock clock) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            if (await bootstrap.AdministratorExistsAsync(context.RequestAborted))
            {
                return Results.NotFound();
            }

            SetupTicketProtector protector = new(dataProtectionProvider);
            if (!context.Request.Cookies.TryGetValue(SetupCookie.Name, out string? cookie)
                || !protector.TryUnprotect(cookie, out SetupTicket? ticket)
                || ticket is null
                || ticket.HasExpired(clock.UtcNow, SetupCookie.Lifetime))
            {
                return Results.BadRequest();
            }

            string optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = ticket.PersonIdentifier.ToString(),
                Name = ticket.Username,
                DisplayName = string.IsNullOrWhiteSpace(ticket.DisplayName) ? ticket.Username : ticket.DisplayName,
            });

            return Results.Content(optionsJson, "application/json");
        });

        endpoints.MapPost(AccountRoutes.RegistrationPasskeyCreationOptions, async (
            HttpContext context,
            SignInManager<Person> signInManager,
            IAntiforgery antiforgery,
            IDataProtectionProvider dataProtectionProvider,
            IClock clock) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            RegistrationTicketProtector protector = new(dataProtectionProvider);
            if (!context.Request.Cookies.TryGetValue(RegistrationCookie.Name, out string? cookie)
                || !protector.TryUnprotect(cookie, out RegistrationTicket? ticket)
                || ticket is null
                || ticket.HasExpired(clock.UtcNow, RegistrationCookie.Lifetime))
            {
                return Results.BadRequest();
            }

            string optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = ticket.PersonIdentifier.ToString(),
                Name = ticket.Username,
                DisplayName = string.IsNullOrWhiteSpace(ticket.DisplayName) ? ticket.Username : ticket.DisplayName,
            });

            return Results.Content(optionsJson, "application/json");
        });

        return endpoints;
    }
}
