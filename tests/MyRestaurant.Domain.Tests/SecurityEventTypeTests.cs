using MyRestaurant.Domain.Authentication;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class SecurityEventTypeTests
{
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
    [InlineData("Sign_In_Succeeded")]
    [InlineData("signed_in")]
    [InlineData("totp_enroled")]
    public void IsKnown_FalseForAnythingElse(string eventType)
        => Assert.False(SecurityEventType.IsKnown(eventType));
}
