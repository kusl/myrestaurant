using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess.Identity;

namespace MyRestaurant.WebApplication.Identity;

public static class RestaurantClaimTypes
{
    public const string DisplayName = "myrestaurant:display_name";

    public const string MustChangePassword = "myrestaurant:must_change_password";

    public const string MustEnrollTotp = "myrestaurant:must_enroll_totp";
}

public sealed class RestaurantClaimsPrincipalFactory : UserClaimsPrincipalFactory<Person>
{
    public RestaurantClaimsPrincipalFactory(
        UserManager<Person> userManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Person user)
    {
        ClaimsIdentity identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        foreach (string role in await UserManager.GetRolesAsync(user).ConfigureAwait(false))
        {
            identity.AddClaim(new Claim(Options.ClaimsIdentity.RoleClaimType, role));
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(RestaurantClaimTypes.DisplayName, user.DisplayName));
        }

        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(RestaurantClaimTypes.MustChangePassword, "true"));
        }

        if (user.MustEnrollTotp)
        {
            identity.AddClaim(new Claim(RestaurantClaimTypes.MustEnrollTotp, "true"));
        }

        return identity;
    }
}
