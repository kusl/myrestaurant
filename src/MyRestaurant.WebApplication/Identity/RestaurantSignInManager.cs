using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Identity;

public sealed class RestaurantSignInManager : SignInManager<Person>
{
    private const string PasswordMethod = "password";

    private const string PasskeyMethod = "passkey";

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

    public override async Task<bool> CanSignInAsync(Person user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.IsActive && await base.CanSignInAsync(user).ConfigureAwait(false);
    }

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
            RecordMetric(SignInAttemptResult.Failed, PasswordMethod);
            return SignInResult.Failed;
        }

        return await PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure).ConfigureAwait(false);
    }

    public override async Task<SignInResult> PasswordSignInAsync(
        Person user,
        string password,
        bool isPersistent,
        bool lockoutOnFailure)
    {
        SignInResult result = await base
            .PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure)
            .ConfigureAwait(false);

        await AuditAsync(user, result, PasswordMethod).ConfigureAwait(false);
        return result;
    }

    public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(
        string code,
        bool isPersistent,
        bool rememberClient)
    {
        Person? user = await GetTwoFactorAuthenticationUserAsync().ConfigureAwait(false);
        SignInResult result = await base
            .TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient)
            .ConfigureAwait(false);

        await AuditAsync(user, result, PasswordMethod).ConfigureAwait(false);
        return result;
    }

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        Person? user = await GetTwoFactorAuthenticationUserAsync().ConfigureAwait(false);
        SignInResult result = await base
            .TwoFactorRecoveryCodeSignInAsync(recoveryCode)
            .ConfigureAwait(false);

        await AuditAsync(user, result, PasswordMethod).ConfigureAwait(false);

        if (result.Succeeded && user is not null)
        {
            await _securityEventLog
                .RecordAsync(user.PersonIdentifier, actorPersonIdentifier: null, SecurityEventType.RecoveryCodeUsed)
                .ConfigureAwait(false);
        }

        return result;
    }

    public override async Task<SignInResult> PasskeySignInAsync(string credentialJson)
    {
        PasskeyAssertionResult<Person> assertion = await PerformPasskeyAssertionAsync(credentialJson).ConfigureAwait(false);
        if (!assertion.Succeeded || assertion.User is null || assertion.Passkey is null)
        {
            RecordMetric(SignInAttemptResult.Failed, PasskeyMethod);
            return SignInResult.Failed;
        }

        Person user = assertion.User;

        SignInResult? preCheck = await PreSignInCheck(user).ConfigureAwait(false);
        if (preCheck is not null)
        {
            await AuditAsync(user, preCheck, PasskeyMethod).ConfigureAwait(false);
            return preCheck;
        }

        IdentityResult updated = await UserManager.AddOrUpdatePasskeyAsync(user, assertion.Passkey).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            await AuditAsync(user, SignInResult.Failed, PasskeyMethod).ConfigureAwait(false);
            return SignInResult.Failed;
        }

        SignInResult result = await SignInOrTwoFactorAsync(user, isPersistent: false, bypassTwoFactor: true).ConfigureAwait(false);
        await AuditAsync(user, result, PasskeyMethod).ConfigureAwait(false);
        return result;
    }

    private async Task AuditAsync(Person? user, SignInResult result, string method)
    {
        SignInAttemptResult attempt = Classify(result);
        RecordMetric(attempt, method);

        if (user is null)
        {
            return;
        }

        string? eventType = SignInAudit.SecurityEventFor(attempt);
        if (eventType is not null)
        {
            await _securityEventLog
                .RecordAsync(user.PersonIdentifier, actorPersonIdentifier: null, eventType)
                .ConfigureAwait(false);
        }
    }

    private void RecordMetric(SignInAttemptResult attempt, string method)
    {
        string? metricResult = SignInAudit.MetricResultFor(attempt);
        if (metricResult is not null)
        {
            _metrics.RecordSignIn(method, metricResult);
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
