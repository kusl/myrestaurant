namespace MyRestaurant.Domain.Authentication;

public static class SecurityEventType
{
    public const string AccountCreated = "account_created";
    public const string AccountDeactivated = "account_deactivated";
    public const string AccountReactivated = "account_reactivated";

    public const string PasswordChanged = "password_changed";
    public const string PasswordResetByAdministrator = "password_reset_by_administrator";
    public const string ForcedPasswordChangeCompleted = "forced_password_change_completed";

    public const string TotpEnrolled = "totp_enrolled";
    public const string TotpRemoved = "totp_removed";
    public const string TotpClearedByAdministrator = "totp_cleared_by_administrator";
    public const string ForcedTotpEnrollmentCompleted = "forced_totp_enrollment_completed";
    public const string RecoveryCodeUsed = "recovery_code_used";
    public const string RecoveryCodesRegenerated = "recovery_codes_regenerated";

    public const string PasskeyRegistered = "passkey_registered";
    public const string PasskeyRemoved = "passkey_removed";

    public const string RoleGranted = "role_granted";
    public const string RoleRevoked = "role_revoked";

    public const string SignInSucceeded = "sign_in_succeeded";
    public const string SignInFailed = "sign_in_failed";
    public const string AccountLockedOut = "account_locked_out";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AccountCreated,
        AccountDeactivated,
        AccountReactivated,
        PasswordChanged,
        PasswordResetByAdministrator,
        ForcedPasswordChangeCompleted,
        TotpEnrolled,
        TotpRemoved,
        TotpClearedByAdministrator,
        ForcedTotpEnrollmentCompleted,
        RecoveryCodeUsed,
        RecoveryCodesRegenerated,
        PasskeyRegistered,
        PasskeyRemoved,
        RoleGranted,
        RoleRevoked,
        SignInSucceeded,
        SignInFailed,
        AccountLockedOut,
    };

    public static bool IsKnown(string eventType) => All.Contains(eventType);
}
