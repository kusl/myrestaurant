namespace MyRestaurant.Domain.Authentication;

public enum SignInAttemptResult
{
    Succeeded,
    Failed,
    LockedOut,
    RequiresTwoFactor,
    NotAllowed,
}

public static class SignInAudit
{
    public static string? SecurityEventFor(SignInAttemptResult result) => result switch
    {
        SignInAttemptResult.Succeeded => SecurityEventType.SignInSucceeded,
        SignInAttemptResult.LockedOut => SecurityEventType.AccountLockedOut,
        SignInAttemptResult.Failed => SecurityEventType.SignInFailed,
        SignInAttemptResult.NotAllowed => SecurityEventType.SignInFailed,
        SignInAttemptResult.RequiresTwoFactor => null,
        _ => null,
    };

    public static string? MetricResultFor(SignInAttemptResult result) => result switch
    {
        SignInAttemptResult.Succeeded => MetricSucceeded,
        SignInAttemptResult.Failed => MetricFailed,
        SignInAttemptResult.LockedOut => MetricFailed,
        SignInAttemptResult.NotAllowed => MetricFailed,
        SignInAttemptResult.RequiresTwoFactor => null,
        _ => null,
    };

    public const string MetricSucceeded = "succeeded";

    public const string MetricFailed = "failed";
}
