using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

/// <summary>
/// The §16.3 end-to-end scenario matrix (TECHNICAL_SPECIFICATION), version-controlled from M1 as
/// skipped placeholders and implemented with Playwright at M6. Each fact below names one required
/// scenario; the Skip reason is uniform because none can run until the browsers are installed and a
/// live instance is available (BUILD_PROGRESS: container-dependent tests).
///
/// The virtual-authenticator (WebAuthn) and window-boundary timing scenarios in particular need the
/// Playwright CDP session and a controllable clock, which arrive with the M6 harness.
/// </summary>
public sealed class EndToEndScenarios
{
    private const string PendingM6 = "End-to-end scenarios are implemented at M6 (needs Playwright browsers and a live instance).";

    [Fact(Skip = PendingM6)]
    public void Setup_BootstrapsFirstAdministratorThenBecomes404()
    {
        // 1. Fresh stack → /setup (passkey via virtual authenticator, TOTP, admin granted) → /setup now 404.
    }

    [Fact(Skip = PendingM6)]
    public void Display_PairsAndShowsRotatingQrAcrossWindowBoundary()
    {
        // 2. Admin creates table → pairing code → device pairs at /display/pair → QR changes across a window boundary.
    }

    [Fact(Skip = PendingM6)]
    public void Guest_ScansRegistersWithPasskeyAndJoins()
    {
        // 3. Guest scans (simulated URL from current token) → registers with passkey → joins; sitting created.
    }

    [Fact(Skip = PendingM6)]
    public void Guest_StagesAddsAndSend_KitchenGetsOneAlert()
    {
        // 4. Guest stages 2 adds + note → Send → kitchen gets one loud alert → lines pending.
    }

    [Fact(Skip = PendingM6)]
    public void SecondGuest_JoinsAndSeesOrderLiveWithRosterUpdate()
    {
        // 5. Second guest joins via fresh token → sees first guest's order live; first guest sees roster update.
    }

    [Fact(Skip = PendingM6)]
    public void Kitchen_FulfillsLine_GuestSeesFulfilledBadge()
    {
        // 6. Kitchen fulfills one line → guest sees fulfilled badge.
    }

    [Fact(Skip = PendingM6)]
    public void Guest_RemoveFulfilledLineRejected_RemovePendingSucceeds()
    {
        // 7. Guest removes fulfilled line → whole batch rejected with per-op reason; removing pending line succeeds.
    }

    [Fact(Skip = PendingM6)]
    public void Send_UnfulfilledPastThreshold_YieldsExactlyOneReminder()
    {
        // 8. A send sits unfulfilled 60 s → exactly one reminder alert.
    }

    [Fact(Skip = PendingM6)]
    public void Counter_AdjustsPriceWithReason_GuestSeesOldToNew()
    {
        // 9. Counter adjusts a price with reason → guest sees old → new with reason.
    }

    [Fact(Skip = PendingM6)]
    public void Counter_ClosesSitting_TableFlipsToSettledAndTotalsMatch()
    {
        // 10. Counter closes (pending-line warning) → table flips to settled read-only; totals match.
    }

    [Fact(Skip = PendingM6)]
    public void Guest_HidesClosedOrder_AdminCanUnhide()
    {
        // 11. Guest hides a closed order → gone from own history (staff/admin unchanged); admin unhides.
    }

    [Fact(Skip = PendingM6)]
    public void Admin_ResetsTotpUser_ForcesPasswordThenTotpReenrollment()
    {
        // 12. Admin resets TOTP-enrolled user → password sign-in → forced password change → forced TOTP re-enroll → home.
    }

    [Fact(Skip = PendingM6)]
    public void PasskeySignIn_OfTotpUser_SkipsTotpChallenge()
    {
        // 13. Passkey sign-in of a TOTP-enrolled user → no TOTP challenge.
    }

    [Fact(Skip = PendingM6)]
    public void JoinToken_ExpiredShowsFriendlyPage_PreviousWindowAccepted()
    {
        // 14. Expired token URL → friendly expiry page; token from previous window → accepted.
    }

    [Fact(Skip = PendingM6)]
    public void Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks()
    {
        // 15. Admin rotates a table's join secret → in-flight token dies; display's next window works.
    }
}
