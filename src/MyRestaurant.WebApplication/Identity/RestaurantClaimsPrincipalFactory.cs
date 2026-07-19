using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess.Identity;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The application-specific claim types, all prefixed so they can never collide with framework or
/// standard claims. Values are set at sign-in by <see cref="RestaurantClaimsPrincipalFactory"/> and
/// refreshed whenever the principal is rebuilt — an explicit <c>RefreshSignInAsync</c> after an
/// obligation completes, and the 5-minute security-stamp revalidation (§3.1).
/// </summary>
public static class RestaurantClaimTypes
{
    /// <summary>The optional human display name (roster / kitchen-queue rendering, §3.1).</summary>
    public const string DisplayName = "myrestaurant:display_name";

    /// <summary>Present (value <c>"true"</c>) while <c>must_change_password</c> is set (§3.5).</summary>
    public const string MustChangePassword = "myrestaurant:must_change_password";

    /// <summary>Present (value <c>"true"</c>) while <c>must_enroll_totp</c> is set (§3.5).</summary>
    public const string MustEnrollTotp = "myrestaurant:must_enroll_totp";
}

/// <summary>
/// Builds the <see cref="ClaimsPrincipal"/> issued into the authentication cookie
/// (TECHNICAL_SPECIFICATION §3.5, §3.7). Three additions over the framework base:
///
/// <list type="bullet">
///   <item><b>Role claims.</b> The single-generic
///   <see cref="UserClaimsPrincipalFactory{TUser}"/> — the one <c>AddIdentityCore</c> registers —
///   never emits role claims; only the two-generic <c>TUser, TRole</c> variant does, and this
///   application deliberately has no role entity or <c>RoleManager</c> (roles are plain strings,
///   §3.7). Without this override the §3.7 area policies could never pass. One role claim is added
///   per granted role, using the configured <see cref="ClaimsIdentityOptions.RoleClaimType"/> so
///   <see cref="ClaimsPrincipal.IsInRole"/> and <c>RequireRole</c> match it.</item>
///   <item><b>Obligation claims.</b> <c>must_change_password</c> / <c>must_enroll_totp</c> travel as
///   claims so <see cref="ObligationsMiddleware"/> enforces §3.5 without a database read per
///   request. They refresh on explicit re-sign-in after an obligation clears and, at the latest,
///   on the 5-minute stamp revalidation.</item>
///   <item><b>Display name</b>, when the person has one.</item>
/// </list>
/// </summary>
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

        // Roles → claims (see the type remarks: the base single-generic factory does not do this).
        foreach (string role in await UserManager.GetRolesAsync(user).ConfigureAwait(false))
        {
            identity.AddClaim(new Claim(Options.ClaimsIdentity.RoleClaimType, role));
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(RestaurantClaimTypes.DisplayName, user.DisplayName));
        }

        // Obligations are present-or-absent flags: absence means "cleared", so the middleware's
        // no-obligation fast path allocates nothing.
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
