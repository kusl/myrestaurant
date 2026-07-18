namespace MyRestaurant.Domain.Authentication;

/// <summary>
/// The terminal-ish outcome of a single sign-in attempt, distilled from ASP.NET Core Identity's
/// <c>SignInResult</c> into a BCL-only value the domain can reason about. The web layer maps the
/// framework result onto this; the domain decides what to audit. Keeping the decision here (rather
/// than inside the <c>SignInManager</c> subclass) makes it exhaustively unit-testable, exactly as the
/// obligations pipeline is (TECHNICAL_SPECIFICATION §3.5, §16.1).
/// </summary>
public enum SignInAttemptResult
{
    /// <summary>Fully authenticated — the cookie was issued.</summary>
    Succeeded,

    /// <summary>Credentials were rejected (wrong password, wrong/absent second factor).</summary>
    Failed,

    /// <summary>The account is locked out (either already, or this attempt tripped the threshold, §3.1).</summary>
    LockedOut,

    /// <summary>The password was correct but a second factor is still required — not yet a terminal outcome.</summary>
    RequiresTwoFactor,

    /// <summary>Sign-in is not permitted for this account (e.g. a confirmation gate, unused here).</summary>
    NotAllowed,
}

/// <summary>
/// The pure audit decision for a sign-in attempt (TECHNICAL_SPECIFICATION §3.5, §12): given the
/// outcome, which <see cref="SecurityEventType"/> row (if any) should be written, and which
/// <c>result</c> label the <c>sign_ins_total</c> metric should carry (if any).
///
/// <para><see cref="SignInAttemptResult.RequiresTwoFactor"/> is deliberately silent on both: the
/// password step passing is not a completed sign-in, so the terminal event/metric is recorded by the
/// second-factor step instead. Everything else is terminal and is both audited and metered.</para>
/// </summary>
public static class SignInAudit
{
    /// <summary>
    /// The security-event type to persist for <paramref name="result"/>, or <c>null</c> when nothing
    /// should be recorded yet (the two-factor challenge case).
    /// </summary>
    public static string? SecurityEventFor(SignInAttemptResult result) => result switch
    {
        SignInAttemptResult.Succeeded => SecurityEventType.SignInSucceeded,
        SignInAttemptResult.LockedOut => SecurityEventType.AccountLockedOut,
        SignInAttemptResult.Failed => SecurityEventType.SignInFailed,
        SignInAttemptResult.NotAllowed => SecurityEventType.SignInFailed,
        SignInAttemptResult.RequiresTwoFactor => null,
        _ => null,
    };

    /// <summary>
    /// The <c>result</c> tag value for the <c>sign_ins_total{method,result}</c> metric (§12), or
    /// <c>null</c> when the attempt should not be metered yet (the two-factor challenge case). A
    /// lockout is metered as a failure while being audited as its own <c>account_locked_out</c> event.
    /// </summary>
    public static string? MetricResultFor(SignInAttemptResult result) => result switch
    {
        SignInAttemptResult.Succeeded => MetricSucceeded,
        SignInAttemptResult.Failed => MetricFailed,
        SignInAttemptResult.LockedOut => MetricFailed,
        SignInAttemptResult.NotAllowed => MetricFailed,
        SignInAttemptResult.RequiresTwoFactor => null,
        _ => null,
    };

    /// <summary>The <c>result</c> tag value recorded on a successful sign-in.</summary>
    public const string MetricSucceeded = "succeeded";

    /// <summary>The <c>result</c> tag value recorded on any non-success terminal outcome.</summary>
    public const string MetricFailed = "failed";
}
