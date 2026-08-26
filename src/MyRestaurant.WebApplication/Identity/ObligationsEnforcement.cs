using System.Security.Claims;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Time;

namespace MyRestaurant.WebApplication.Identity;

public static class AccountRoutes
{
    public const string SignIn = "/sign-in";

    public const string SignInTwoFactor = "/sign-in/two-factor";

    public const string SignInRecoveryCode = "/sign-in/recovery-code";

    public const string SignOut = "/sign-out";

    public const string Register = "/register";

    public const string RegistrationPasskeyCreationOptions = "/register/passkey/creation-options";

    public const string AccessDenied = "/access-denied";

    public const string ForcedPasswordChange = "/account/change-password-required";

    public const string ForcedTotpEnrollment = "/account/enroll-totp-required";

    public const string Profile = "/account";

    public const string ChangePassword = "/account/change-password";

    public const string TotpEnrollment = "/account/enroll-totp";

    public const string Passkeys = "/account/passkeys";

    public const string PasskeyCreationOptions = "/account/passkey/creation-options";

    public const string PasskeyRequestOptions = "/account/passkey/request-options";

    public const string Setup = "/setup";

    public const string SetupPasskeyCreationOptions = "/setup/passkey/creation-options";
}

public static class ObligationsEnforcement
{
    public static PostAuthenticationObligation NextObligationFor(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return ObligationsPipeline.NextObligation(
            HasObligationClaim(principal, RestaurantClaimTypes.MustChangePassword),
            HasObligationClaim(principal, RestaurantClaimTypes.MustEnrollTotp));
    }

    public static bool IsExemptPath(PathString path)
        => path.StartsWithSegments(AccountRoutes.ForcedPasswordChange)
        || path.StartsWithSegments(AccountRoutes.ForcedTotpEnrollment)
        || path.StartsWithSegments(AccountRoutes.SignOut)
        || path.StartsWithSegments(AccountRoutes.AccessDenied)
        || path.StartsWithSegments(RestaurantClockRoutes.Snapshot)
        || path.StartsWithSegments(SourceRoutes.Source)
        || path.StartsWithSegments("/healthz")
        || path.StartsWithSegments("/_framework");

    public static string PageFor(PostAuthenticationObligation obligation) => obligation switch
    {
        PostAuthenticationObligation.ForcePasswordChange => AccountRoutes.ForcedPasswordChange,
        PostAuthenticationObligation.ForceTotpEnrollment => AccountRoutes.ForcedTotpEnrollment,
        PostAuthenticationObligation.None => throw new ArgumentOutOfRangeException(
            nameof(obligation), obligation, "No page exists when no obligation is outstanding."),
        _ => throw new ArgumentOutOfRangeException(nameof(obligation), obligation, "Unknown obligation."),
    };

    public static string RedirectTargetFor(PostAuthenticationObligation obligation, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string destination = $"{request.PathBase}{request.Path}{request.QueryString}";
        if (string.IsNullOrEmpty(destination))
        {
            destination = "/";
        }

        return $"{PageFor(obligation)}?ReturnUrl={Uri.EscapeDataString(destination)}";
    }

    public static string SafeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return "/";
        }

        bool isLocal = returnUrl[0] == '/'
            && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal);

        return isLocal ? returnUrl : "/";
    }

    private static bool HasObligationClaim(ClaimsPrincipal principal, string claimType)
        => string.Equals(principal.FindFirstValue(claimType), "true", StringComparison.OrdinalIgnoreCase);
}
