using System.Security.Cryptography;
using Microsoft.Playwright;
using MyRestaurant.Domain.Security;
using MyRestaurant.EndToEnd.Tests.Harness;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

/// <summary>
/// The §16.3 end-to-end scenario matrix (TECHNICAL_SPECIFICATION), version-controlled from M1 as
/// skipped placeholders and implemented against a real browser from M6 Slice 2 onwards.
///
/// <para>Three are live: scenario 1 (the first-administrator bootstrap, including a real WebAuthn
/// attestation and a real TOTP confirmation), scenario 13 (a passkey sign-in of a TOTP-enrolled person
/// must not be challenged for a code), and scenario 14 (the join-token window arithmetic as a guest
/// experiences it). They come first because between them they exercise the whole harness — process,
/// database, browser, virtual authenticator, and the domain's own token computation — which is what
/// the remaining twelve are waiting on rather than any of their own machinery.</para>
///
/// <para>Every scenario begins with <see cref="SkipUnlessHarnessAvailable"/>. The scenarios are opt-in
/// (<c>MYRESTAURANT_E2E=1</c>) and additionally need a container engine, a Chromium build and a
/// current build of the web application; each absence is a skip with the fix in its message. See
/// <see cref="RestaurantHarness"/>.</para>
/// </summary>
public sealed class EndToEndScenarios : IClassFixture<RestaurantHarness>
{
    private const string PendingHarnessExtension =
        "Awaiting a later M6 slice: the harness is in place (Harness/RestaurantHarness.cs), but this"
        + " scenario needs surface plumbing it does not have yet.";

    private readonly RestaurantHarness _harness;

    public EndToEndScenarios(RestaurantHarness harness) => _harness = harness;

    // -------------------------------------------------------------------------------------------
    // 1. Fresh stack → /setup bootstrap (passkey via virtual authenticator, TOTP, admin granted)
    //    → /setup now 404.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Setup_BootstrapsFirstAdministratorThenBecomes404()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);
        IPage page = instance.Page;

        // The gate is open on an empty database, and the wizard starts at step one.
        IResponse? open = await page.GotoAsync(AccountRoutes.Setup);
        Assert.NotNull(open);
        Assert.Equal(200, open.Status);
        Assert.Equal("Create the first administrator", await HeadingAsync(page));

        IReadOnlyList<string> recoveryCodes =
            await AccountJourneys.CompleteSetupAsync(page, AccountJourneys.DefaultAdministrator);

        Assert.Equal("You are the administrator", await HeadingAsync(page));
        Assert.Equal(AccountJourneys.ExpectedRecoveryCodeCount, recoveryCodes.Count);
        Assert.All(recoveryCodes, code => Assert.False(string.IsNullOrWhiteSpace(code)));

        // §3.6's own words: "the moment one does, this page returns 404". The panel still renders — a
        // person who bookmarked it deserves a sentence, not a blank error — so the status code is the
        // assertion, not the body.
        IResponse? closed = await page.GotoAsync(AccountRoutes.Setup);
        Assert.NotNull(closed);
        Assert.Equal(404, closed.Status);
    }

    // -------------------------------------------------------------------------------------------
    // 13. Passkey sign-in of a TOTP-enrolled user → no TOTP challenge.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task PasskeySignIn_OfTotpUser_SkipsTotpChallenge()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);
        IPage page = instance.Page;
        AdministratorAccount account = AccountJourneys.DefaultAdministrator;

        // The §3.6 wizard registers a passkey AND enrolls TOTP in one pass, which is exactly the
        // account shape this scenario is about: a person who has both, signing in with one of them.
        await AccountJourneys.CompleteSetupAsync(page, account);
        await AccountJourneys.SignOutAsync(page);
        await AccountJourneys.SignInWithPasskeyAsync(page, account.Username);

        // The whole point (§3.5, ADR-0010): TOTP guards the password path only. PasskeySignInAsync
        // cannot return RequiresTwoFactor by construction, and this is the observable consequence.
        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, page.Url);
        Assert.DoesNotContain(AccountRoutes.SignInRecoveryCode, page.Url);

        // And it is a real session, not merely a page that failed to redirect.
        ILocator sessionName = page.Locator("span.session-name").First;
        await sessionName.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        Assert.Equal(account.Username, (await sessionName.InnerTextAsync()).Trim());
    }

    // -------------------------------------------------------------------------------------------
    // 14. Expired token URL → friendly expiry page; token from previous window → accepted.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task JoinToken_ExpiredShowsFriendlyPage_PreviousWindowAccepted()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const int rotationSeconds = RestaurantInstance.DefaultTableJoinTokenRotationSeconds;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(rotationSeconds, cancellationToken);
        IPage page = instance.Page;

        // Real signing material, held only here and in the row — the server reads it back through
        // ITableJoinSecretReader, so a token computed here is a token it will genuinely verify.
        byte[] joinSecret = RandomNumberGenerator.GetBytes(32);
        Guid tableIdentifier = await instance.InsertActiveTableAsync(
            "E2E Fourteen", joinSecret, cancellationToken);

        long currentWindow = JoinTokenService.CurrentWindowIndex(DateTimeOffset.UtcNow, rotationSeconds);

        // (a) Four windows back is inside §4.3's bounded lookback, so it is classified Expired rather
        // than Invalid — and §4.4 renders one friendly page for every failure, with no oracle detail
        // about which thing went wrong. Deliberately first: it must write no grant.
        string staleToken = JoinTokenService.ComputeToken(joinSecret, tableIdentifier, currentWindow - 4);
        await page.GotoAsync(JoinPath(tableIdentifier, staleToken));

        Assert.Equal("That code has expired", await HeadingAsync(page));
        Assert.DoesNotContain(AccountRoutes.SignIn, page.Url);

        // (b) The immediately previous window is accepted (§4.3: "the current and previous window"), so
        // the grant is written and an anonymous scanner is sent to sign in with this table as the
        // return URL. That redirect IS the acceptance — it is what the guest flow does next (§4.4).
        string previousWindowToken =
            JoinTokenService.ComputeToken(joinSecret, tableIdentifier, currentWindow - 1);
        await page.GotoAsync(JoinPath(tableIdentifier, previousWindowToken));

        Assert.Contains(AccountRoutes.SignIn, page.Url);
        Assert.Contains(tableIdentifier.ToString("D"), page.Url);
    }

    // -------------------------------------------------------------------------------------------
    // Still to come. Each is one required §16.3 scenario, named so the matrix stays legible.
    // -------------------------------------------------------------------------------------------

    [Fact(Skip = PendingHarnessExtension)]
    public void Display_PairsAndShowsRotatingQrAcrossWindowBoundary()
    {
        // 2. Admin creates table → pairing code → device pairs at /display/pair → QR changes across a
        //    window boundary. Needs a short TABLE_JOIN_TOKEN_ROTATION_SECONDS instance (the harness
        //    already takes one) and a second browser context for the display device's own principal.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Guest_ScansRegistersWithPasskeyAndJoins()
    {
        // 3. Guest scans (simulated URL from current token) → registers with passkey → joins; sitting
        //    created. Needs the guest registration journey, which is not the same page as /setup.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Guest_StagesAddsAndSend_KitchenGetsOneAlert()
    {
        // 4. Guest stages 2 adds + note → Send → kitchen gets one loud alert → lines pending.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void SecondGuest_JoinsAndSeesOrderLiveWithRosterUpdate()
    {
        // 5. Second guest joins via fresh token → sees first guest's order live; first guest sees
        //    roster update. Needs two live circuits in two contexts at once.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Kitchen_FulfillsLine_GuestSeesFulfilledBadge()
    {
        // 6. Kitchen fulfills one line → guest sees fulfilled badge.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Guest_RemoveFulfilledLineRejected_RemovePendingSucceeds()
    {
        // 7. Guest removes fulfilled line → whole batch rejected with per-op reason; removing pending
        //    line succeeds.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Send_UnfulfilledPastThreshold_YieldsExactlyOneReminder()
    {
        // 8. A send sits unfulfilled 60 s → exactly one reminder alert. Wants a short
        //    KITCHEN_SUBMISSION_REMINDER_SECONDS rather than sixty seconds of waiting.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Counter_AdjustsPriceWithReason_GuestSeesOldToNew()
    {
        // 9. Counter adjusts a price with reason → guest sees old → new with reason.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Counter_ClosesSitting_TableFlipsToSettledAndTotalsMatch()
    {
        // 10. Counter closes (pending-line warning) → table flips to settled read-only; totals match.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Guest_HidesClosedOrder_AdminCanUnhide()
    {
        // 11. Guest hides a closed order → gone from own history (staff/admin unchanged); admin
        //     filters the hidden-records view by username → Unhide restores it.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Admin_ResetsTotpUser_ForcesPasswordThenTotpReenrollment()
    {
        // 12. Admin resets TOTP-enrolled user → password sign-in → forced password change → forced
        //     TOTP re-enroll → lands home; the passkey path hits the pipeline too.
    }

    [Fact(Skip = PendingHarnessExtension)]
    public void Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks()
    {
        // 15. Admin rotates a table's join secret → in-flight token dies; display's next window works.
    }

    // --- helpers ---------------------------------------------------------------------------------

    private void SkipUnlessHarnessAvailable()
        => Assert.SkipUnless(
            _harness.SkipReason is null,
            _harness.SkipReason ?? "The end-to-end harness is unavailable.");

    /// <summary>
    /// The page's single <c>h1</c>, trimmed. Every surface has exactly one — <c>Routes.razor</c>'s
    /// <c>FocusOnNavigate</c> depends on that too — so this doubles as a check that it still holds.
    /// </summary>
    private static async Task<string> HeadingAsync(IPage page)
        => (await page.Locator("h1").First.InnerTextAsync()).Trim();

    private static string JoinPath(Guid tableIdentifier, string token)
        => $"/table/{tableIdentifier:D}?token={Uri.EscapeDataString(token)}";
}
