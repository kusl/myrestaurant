using MyRestaurant.Domain.Authentication;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Guards the security-event vocabulary (TECHNICAL_SPECIFICATION §3.4–§3.7, §8.2). The set here must
/// stay identical to the <c>security_event.event_type</c> CHECK in the migration, so this test pins
/// the exact strings and count; a drift on either side (a typo, a forgotten value) fails here rather
/// than as a PostgreSQL CHECK violation at write time.
/// </summary>
public sealed class SecurityEventTypeTests
{
    // The exact set the schema's CHECK constraint allows (0001_initial_schema.sql, security_event).
    private static readonly string[] SchemaEventTypes =
    [
        "account_created",
        "account_deactivated",
        "account_reactivated",
        "password_changed",
        "password_reset_by_administrator",
        "forced_password_change_completed",
        "totp_enrolled",
        "totp_removed",
        "totp_cleared_by_administrator",
        "forced_totp_enrollment_completed",
        "recovery_code_used",
        "recovery_codes_regenerated",
        "passkey_registered",
        "passkey_removed",
        "role_granted",
        "role_revoked",
        "sign_in_succeeded",
        "sign_in_failed",
        "account_locked_out",
    ];

    [Fact]
    public void All_MatchesTheSchemaCheckSet()
        => Assert.Equal(SchemaEventTypes.OrderBy(x => x), SecurityEventType.All.OrderBy(x => x));

    [Fact]
    public void All_HasNineteenEntries()
        => Assert.Equal(19, SecurityEventType.All.Count);

    [Theory]
    [InlineData("sign_in_succeeded")]
    [InlineData("sign_in_failed")]
    [InlineData("account_locked_out")]
    [InlineData("role_granted")]
    public void IsKnown_TrueForSchemaValues(string eventType)
        => Assert.True(SecurityEventType.IsKnown(eventType));

    [Theory]
    [InlineData("")]
    [InlineData("Sign_In_Succeeded")] // case-sensitive: the column stores exact lower-case tokens
    [InlineData("signed_in")]
    [InlineData("totp_enroled")]      // misspelling must not slip past the guard
    public void IsKnown_FalseForAnythingElse(string eventType)
        => Assert.False(SecurityEventType.IsKnown(eventType));
}
