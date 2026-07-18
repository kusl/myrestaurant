namespace MyRestaurant.Domain.Authentication;

/// <summary>
/// The closed vocabulary of security-audit event types (TECHNICAL_SPECIFICATION §3.4–§3.7). These
/// strings are the exact values allowed by the <c>security_event.event_type</c> CHECK constraint
/// (§8.2); keeping them in one BCL-only place lets the web layer raise events, the data layer persist
/// them, and a unit test assert the set matches the schema — all without duplicating string literals.
///
/// <para>Do not rename a value without a coordinated migration: the database rejects any string not in
/// this list, so a typo here surfaces as a CHECK violation at write time rather than a compile error.
/// <see cref="IsKnown"/> gives callers a cheap client-side guard so a bad value fails fast with a clear
/// exception instead of a raw PostgreSQL error.</para>
/// </summary>
public static class SecurityEventType
{
    // Account lifecycle (§3.7).
    public const string AccountCreated = "account_created";
    public const string AccountDeactivated = "account_deactivated";
    public const string AccountReactivated = "account_reactivated";

    // Passwords (§3.2, §3.5, §3.7).
    public const string PasswordChanged = "password_changed";
    public const string PasswordResetByAdministrator = "password_reset_by_administrator";
    public const string ForcedPasswordChangeCompleted = "forced_password_change_completed";

    // TOTP + recovery codes (§3.4, §3.5, §3.7).
    public const string TotpEnrolled = "totp_enrolled";
    public const string TotpRemoved = "totp_removed";
    public const string TotpClearedByAdministrator = "totp_cleared_by_administrator";
    public const string ForcedTotpEnrollmentCompleted = "forced_totp_enrollment_completed";
    public const string RecoveryCodeUsed = "recovery_code_used";
    public const string RecoveryCodesRegenerated = "recovery_codes_regenerated";

    // Passkeys (§3.3).
    public const string PasskeyRegistered = "passkey_registered";
    public const string PasskeyRemoved = "passkey_removed";

    // Roles (§3.6, §3.7).
    public const string RoleGranted = "role_granted";
    public const string RoleRevoked = "role_revoked";

    // Sign-in outcomes (§3.5). The metric sign_ins_total{method,result} mirrors these (§12).
    public const string SignInSucceeded = "sign_in_succeeded";
    public const string SignInFailed = "sign_in_failed";
    public const string AccountLockedOut = "account_locked_out";

    /// <summary>Every valid <c>event_type</c>, in the order the schema lists them.</summary>
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

    /// <summary>True when <paramref name="eventType"/> is one the schema will accept (case-sensitive).</summary>
    public static bool IsKnown(string eventType) => All.Contains(eventType);
}
