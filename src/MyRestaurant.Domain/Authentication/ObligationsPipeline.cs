namespace MyRestaurant.Domain.Authentication;

/// <summary>
/// The single obligation an authenticated principal must clear before reaching any
/// destination (TECHNICAL_SPECIFICATION §3.5, ADR-0010).
/// </summary>
public enum PostAuthenticationObligation
{
    /// <summary>Nothing outstanding — continue to the requested URL or role home.</summary>
    None,

    /// <summary><c>must_change_password</c> is set — force the password-change page first.</summary>
    ForcePasswordChange,

    /// <summary><c>must_enroll_totp</c> is set — force TOTP enrollment (with fresh recovery codes).</summary>
    ForceTotpEnrollment,
}

/// <summary>
/// The post-authentication obligations pipeline: after either sign-in path succeeds (and the
/// passkey path never sees a TOTP challenge — that is a separate step), the principal must
/// clear obligations in a fixed order before any other authenticated endpoint is reachable.
/// This is the pure decision the authorization filter/middleware enforces (§3.5).
///
/// Order is deliberate: a forced password change comes before forced TOTP enrollment, because
/// an administrator reset wipes the password and, if TOTP was enrolled, the TOTP secret too —
/// the user re-establishes the password, then re-enrolls TOTP.
/// </summary>
public static class ObligationsPipeline
{
    /// <summary>The next obligation given the two account flags; <see cref="PostAuthenticationObligation.None"/> when clear.</summary>
    public static PostAuthenticationObligation NextObligation(bool mustChangePassword, bool mustEnrollTotp)
    {
        if (mustChangePassword)
        {
            return PostAuthenticationObligation.ForcePasswordChange;
        }

        if (mustEnrollTotp)
        {
            return PostAuthenticationObligation.ForceTotpEnrollment;
        }

        return PostAuthenticationObligation.None;
    }

    /// <summary>True when no obligation is outstanding and the principal may proceed to its destination.</summary>
    public static bool IsCleared(bool mustChangePassword, bool mustEnrollTotp)
        => NextObligation(mustChangePassword, mustEnrollTotp) == PostAuthenticationObligation.None;

    /// <summary>
    /// Whether an endpoint is reachable while obligations are pending. Only sign-out and the
    /// pipeline's own pages are reachable; everything else is blocked until the pipeline clears
    /// (§3.5). The caller supplies whether the requested endpoint is one of those exemptions.
    /// </summary>
    public static bool MayReachEndpoint(bool mustChangePassword, bool mustEnrollTotp, bool endpointIsPipelineOrSignOut)
        => IsCleared(mustChangePassword, mustEnrollTotp) || endpointIsPipelineOrSignOut;
}
