using System.Security.Claims;
using MyRestaurant.Domain.Authentication;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The routes the post-authentication obligations pipeline (§3.5) and the account surfaces live on.
/// One place, so pages, the middleware, and the cookie configuration can never drift apart.
/// </summary>
public static class AccountRoutes
{
    /// <summary>The password sign-in page (static SSR so the cookie can be written).</summary>
    public const string SignIn = "/sign-in";

    /// <summary>The TOTP challenge on the password path (§3.4, §4.2).</summary>
    public const string SignInTwoFactor = "/sign-in/two-factor";

    /// <summary>A recovery code standing in for the TOTP code (§3.4).</summary>
    public const string SignInRecoveryCode = "/sign-in/recovery-code";

    /// <summary>The sign-out endpoint (POST only, antiforgery-protected).</summary>
    public const string SignOut = "/sign-out";

    /// <summary>Shown when an authenticated principal fails an area policy (§3.7).</summary>
    public const string AccessDenied = "/access-denied";

    /// <summary>The forced password-change page — obligation (1) of §3.5.</summary>
    public const string ForcedPasswordChange = "/account/change-password-required";

    /// <summary>The forced TOTP re-enrollment page — obligation (2) of §3.5.</summary>
    public const string ForcedTotpEnrollment = "/account/enroll-totp-required";
}

/// <summary>
/// The web-layer half of the §3.5 obligations pipeline. The <em>decision</em> is the pure, exhaustively
/// unit-tested <see cref="ObligationsPipeline"/> in the Domain; this class maps a
/// <see cref="ClaimsPrincipal"/>'s obligation claims onto that decision, says which request paths stay
/// reachable while an obligation is outstanding, and builds the redirect that sends the principal to
/// the right page. <see cref="ObligationsMiddleware"/> is a thin shell over these statics so the whole
/// enforcement surface is testable without a server.
/// </summary>
public static class ObligationsEnforcement
{
    /// <summary>
    /// The next outstanding obligation for <paramref name="principal"/>, decided by the Domain
    /// pipeline over the claims <see cref="RestaurantClaimsPrincipalFactory"/> issued at sign-in.
    /// </summary>
    public static PostAuthenticationObligation NextObligationFor(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return ObligationsPipeline.NextObligation(
            HasObligationClaim(principal, RestaurantClaimTypes.MustChangePassword),
            HasObligationClaim(principal, RestaurantClaimTypes.MustEnrollTotp));
    }

    /// <summary>
    /// True when <paramref name="path"/> stays reachable while an obligation is outstanding (§3.5:
    /// "no authenticated endpoint except sign-out and the pipeline pages themselves"). Health probes
    /// and framework static assets are also exempt — they carry no user action. The Blazor circuit
    /// endpoint (<c>/_blazor</c>) is deliberately <b>not</b> exempt: while a flag is set, interactive
    /// circuits are refused too, so an already-open tab cannot keep acting.
    /// </summary>
    public static bool IsExemptPath(PathString path)
        => path.StartsWithSegments(AccountRoutes.ForcedPasswordChange)
        || path.StartsWithSegments(AccountRoutes.ForcedTotpEnrollment)
        || path.StartsWithSegments(AccountRoutes.SignOut)
        || path.StartsWithSegments(AccountRoutes.AccessDenied)
        || path.StartsWithSegments("/healthz")
        || path.StartsWithSegments("/_framework");

    /// <summary>The page that clears <paramref name="obligation"/>.</summary>
    public static string PageFor(PostAuthenticationObligation obligation) => obligation switch
    {
        PostAuthenticationObligation.ForcePasswordChange => AccountRoutes.ForcedPasswordChange,
        PostAuthenticationObligation.ForceTotpEnrollment => AccountRoutes.ForcedTotpEnrollment,
        PostAuthenticationObligation.None => throw new ArgumentOutOfRangeException(
            nameof(obligation), obligation, "No page exists when no obligation is outstanding."),
        _ => throw new ArgumentOutOfRangeException(nameof(obligation), obligation, "Unknown obligation."),
    };

    /// <summary>
    /// The redirect that sends the principal to the obligation page, carrying the originally
    /// requested URL so §3.5 step (3) — "continue to the originally requested URL" — can honour it
    /// once the pipeline clears.
    /// </summary>
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

    /// <summary>
    /// Collapses a caller-supplied return URL to a safe local path: it must start with a single
    /// <c>'/'</c> (no scheme, no protocol-relative <c>//host</c>), otherwise <c>"/"</c> is returned.
    /// Shared by the sign-in pages and the sign-out endpoint so none of them can open-redirect.
    /// </summary>
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
