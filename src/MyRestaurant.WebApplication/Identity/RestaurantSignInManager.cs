using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// A <see cref="SignInManager{TUser}"/> that audits every sign-in outcome centrally
/// (TECHNICAL_SPECIFICATION §3.5, §12): whichever endpoint drives the sign-in, the terminal result is
/// recorded once as a <c>security_event</c> and once on the <c>sign_ins_total{method,result}</c>
/// metric. The <em>decision</em> of what to record lives in the pure <see cref="SignInAudit"/>
/// (unit-tested exhaustively); this class only maps the framework's <see cref="SignInResult"/> onto it
/// and performs the side effects.
///
/// <para>It is also where <b>deactivation blocks sign-in</b> (§3.7, F-10b): <see cref="CanSignInAsync"/>
/// refuses an inactive <see cref="Person"/>, which the framework surfaces as
/// <see cref="SignInResult.NotAllowed"/> — audited as a failed sign-in. Deactivation additionally
/// kills live sessions through the security stamp; this gate closes the front door.</para>
///
/// <para>Only the password and second-factor paths are overridden here. The passkey path
/// (<c>SignInManager.SignInAsync</c> after a WebAuthn assertion, method = <c>passkey</c>) records its
/// own success in the passkey slice, so <see cref="SignInManager{TUser}.SignInAsync(TUser,bool,string)"/>
/// is intentionally not overridden — overriding it would double-count the password path, which calls it
/// internally on success.</para>
///
/// <para>A subject is required for a <c>security_event</c> row (the column is NOT NULL, §8.2), so a
/// failed attempt against a non-existent username is metered but not audited — there is no account to
/// attribute it to. <c>RequiresTwoFactor</c> is neither audited nor metered here: the password step
/// passing is not a completed sign-in, and the second-factor step records the terminal outcome.</para>
/// </summary>
public sealed class RestaurantSignInManager : SignInManager<Person>
{
    /// <summary>The <c>method</c> tag for the password path, including its TOTP/recovery second factor (§3.5).</summary>
    private const string PasswordMethod = "password";

    private readonly ISecurityEventLog _securityEventLog;
    private readonly RestaurantMetrics _metrics;

    public RestaurantSignInManager(
        UserManager<Person> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<Person> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<Person>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<Person> confirmation,
        ISecurityEventLog securityEventLog,
        RestaurantMetrics metrics)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        ArgumentNullException.ThrowIfNull(securityEventLog);
        ArgumentNullException.ThrowIfNull(metrics);

        _securityEventLog = securityEventLog;
        _metrics = metrics;
    }

    /// <summary>
    /// The pre-sign-in gate: a deactivated account may not sign in on any path (§3.7). The framework
    /// turns a <c>false</c> here into <see cref="SignInResult.NotAllowed"/>, which
    /// <see cref="AuditAsync"/> records as a failed sign-in.
    /// </summary>
    public override async Task<bool> CanSignInAsync(Person user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.IsActive && await base.CanSignInAsync(user).ConfigureAwait(false);
    }

    /// <summary>
    /// Username + password. Reimplements the base's "resolve the user, else fail" step so that a
    /// failure against an unknown username is still metered, then routes real users through the
    /// user-typed overload below (which does the auditing) — no double counting.
    /// </summary>
    public override async Task<SignInResult> PasswordSignInAsync(
        string userName,
        string password,
        bool isPersistent,
        bool lockoutOnFailure)
    {
        ArgumentNullException.ThrowIfNull(userName);

        Person? user = await UserManager.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            // No account to attribute a security_event to; still meter the failed attempt (§12).
            RecordMetric(SignInAttemptResult.Failed);
            return SignInResult.Failed;
        }

        return await PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure).ConfigureAwait(false);
    }

    /// <summary>Known user + password: the base does the work; we audit the terminal outcome.</summary>
    public override async Task<SignInResult> PasswordSignInAsync(
        Person user,
        string password,
        bool isPersistent,
        bool lockoutOnFailure)
    {
        SignInResult result = await base
            .PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure)
            .ConfigureAwait(false);

        await AuditAsync(user, result).ConfigureAwait(false);
        return result;
    }

    /// <summary>The TOTP second factor on the password path (§3.5): audit the terminal outcome.</summary>
    public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(
        string code,
        bool isPersistent,
        bool rememberClient)
    {
        // Capture the pending user before the base runs: a successful sign-in clears the two-factor
        // cookie, after which GetTwoFactorAuthenticationUserAsync would return null.
        Person? user = await GetTwoFactorAuthenticationUserAsync().ConfigureAwait(false);
        SignInResult result = await base
            .TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient)
            .ConfigureAwait(false);

        await AuditAsync(user, result).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// A recovery code standing in for the TOTP second factor (§3.4): audit the sign-in outcome, and —
    /// on success — also record that a recovery code was consumed. The base only signs in when the code
    /// was actually redeemed, so a succeeded result means exactly one code was spent.
    /// </summary>
    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        Person? user = await GetTwoFactorAuthenticationUserAsync().ConfigureAwait(false);
        SignInResult result = await base
            .TwoFactorRecoveryCodeSignInAsync(recoveryCode)
            .ConfigureAwait(false);

        await AuditAsync(user, result).ConfigureAwait(false);

        if (result.Succeeded && user is not null)
        {
            await _securityEventLog
                .RecordAsync(user.PersonIdentifier, actorPersonIdentifier: null, SecurityEventType.RecoveryCodeUsed)
                .ConfigureAwait(false);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Meters the attempt and, when there is a subject, records the security event.</summary>
    private async Task AuditAsync(Person? user, SignInResult result)
    {
        SignInAttemptResult attempt = Classify(result);
        RecordMetric(attempt);

        if (user is null)
        {
            return;
        }

        string? eventType = SignInAudit.SecurityEventFor(attempt);
        if (eventType is not null)
        {
            // The subject acted on themselves / the system observed the attempt — no distinct actor.
            await _securityEventLog
                .RecordAsync(user.PersonIdentifier, actorPersonIdentifier: null, eventType)
                .ConfigureAwait(false);
        }
    }

    private void RecordMetric(SignInAttemptResult attempt)
    {
        string? metricResult = SignInAudit.MetricResultFor(attempt);
        if (metricResult is not null)
        {
            _metrics.RecordSignIn(PasswordMethod, metricResult);
        }
    }

    private static SignInAttemptResult Classify(SignInResult result)
    {
        if (result.Succeeded)
        {
            return SignInAttemptResult.Succeeded;
        }

        if (result.IsLockedOut)
        {
            return SignInAttemptResult.LockedOut;
        }

        if (result.IsNotAllowed)
        {
            return SignInAttemptResult.NotAllowed;
        }

        if (result.RequiresTwoFactor)
        {
            return SignInAttemptResult.RequiresTwoFactor;
        }

        return SignInAttemptResult.Failed;
    }
}
