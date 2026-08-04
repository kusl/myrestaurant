using System.Security.Cryptography;
using Microsoft.Playwright;
using MyRestaurant.Domain.Security;
using MyRestaurant.EndToEnd.Tests.Harness;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

/// <summary>
/// The §16.3 end-to-end scenario matrix (TECHNICAL_SPECIFICATION), version-controlled from M1 as
/// skipped placeholders and implemented against a real browser from M6 Slice 2 onwards.
///
/// <para>Eleven are live. M6 Slice 2 brought <b>1</b> (the first-administrator bootstrap, including a real
/// WebAuthn attestation and a real TOTP confirmation), <b>13</b> (a passkey sign-in of a TOTP-enrolled
/// person must not be challenged for a code) and <b>14</b> (the join-token window arithmetic as a guest
/// experiences it) — chosen because between them they exercise the whole harness. M6 Slice 3 adds
/// <b>2</b> (a display pairs and its QR advances across a window boundary) and <b>15</b> (rotating a
/// table's join secret kills every outstanding code and the paired display recovers by itself), which
/// are the two scenarios about the rotating code as a <em>screen</em> rather than as a URL. M6 Slice 5
/// adds <b>3</b> (a guest scans, self-registers with a passkey, and joins on the grant after the code
/// they scanned has expired), which is the first scenario driven entirely by somebody with no account
/// and no role — and the first that needed a product surface built before it could be written. M6
/// Slice 6 adds <b>4</b> (a guest stages two adds and a note, sends, and the kitchen gets exactly one
/// alert with both lines pending) and <b>6</b> (the kitchen marks one line away and the guest's own
/// screen re-badges it), the first two that watch §9's live updates cross between two circuits. M6
/// Slice 9 adds <b>5</b> (a second guest joins, the first guest's roster grows without anyone touching
/// their phone, and the second guest watches the first guest's order grow), the first with two guests
/// in the restaurant at once and the only one where every interesting event is raised by a browser
/// other than the one being asserted on. M6 Slice 10 adds <b>7</b> (a guest holding a tick on a line
/// the kitchen then passes, an all-or-nothing refusal, and the removal that succeeds once the batch is
/// clean), the first about §6.5.9 as a guest experiences it and the first to find that the surface
/// refuses on the guest's behalf before the transaction ever gets the chance. M6 Slice 11 adds
/// <b>8</b> (a send left untouched past the threshold draws one reminder, and only one), the first
/// scenario in the matrix whose subject is something no browser did — and the first whose closing
/// assertion is that nothing further happened, which is a different shape of claim from every other
/// one here.</para>
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

    /// <summary>
    /// The rotation window for the two scenarios that must watch a boundary go past (§13's floor is ten
    /// seconds; the application's own default is sixty). Twenty is a compromise with a reason on each
    /// side: long enough that a paused container or a slow page load cannot push a two-step assertion
    /// across two boundaries, short enough that waiting for the §4.3 refresh is seconds rather than a
    /// minute. Everything either scenario asserts is expressed relative to the window index, so the
    /// exact number is not load-bearing — only its order of magnitude is.
    /// </summary>
    private const int BoundaryWatchingRotationSeconds = 20;

    /// <summary>
    /// §13's floor, used by the one scenario that needs a token to <em>expire</em> during the run
    /// rather than merely to rotate. Ten seconds means the current-and-previous acceptance window
    /// (§4.3) closes twenty-odd seconds after a scan, which is a wait a scenario can afford and a
    /// duration a real registration could plausibly exceed. Nothing else wants it this short: the two
    /// boundary-watching scenarios above need a window that cannot be crossed twice by accident.
    /// </summary>
    private const int ShortestRotationSeconds = RestaurantOptions.MinimumTableJoinTokenRotationSeconds;

    /// <summary>
    /// <c>KITCHEN_SUBMISSION_REMINDER_SECONDS</c> for the one scenario that has to sit through the
    /// threshold. §16.3 writes scenario 8 as "sits unfulfilled 60 s", and §13's floor is 1.
    ///
    /// <para>Five, for a reason on each side. §8.4's scan is what decides a reminder and it compares
    /// <c>occurred_at</c> against a threshold it is handed — the rule is identical at five seconds and
    /// at sixty, so the number is a duration to wait rather than a parameter under test. Shorter than
    /// <c>KitchenReminderService.ScanInterval</c> would be pointless, because the scan's five-second
    /// resolution then dominates and the configured threshold stops being the thing that fires it;
    /// longer buys nothing this scenario asserts on and costs the whole difference in wall clock.</para>
    /// </summary>
    private const int ImpatientReminderSeconds = 5;

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
    // 2. Admin creates table → pairing code → device pairs at /display/pair → /display/{table}
    //    shows a rotating QR that changes across a window boundary.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Display_PairsAndShowsRotatingQrAcrossWindowBoundary()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Two";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(
                BoundaryWatchingRotationSeconds, cancellationToken: cancellationToken);

        // The whole scenario is an administrator's doing, so it needs an administrator; §3.6's wizard is
        // the only way one comes into existence and it signs them in on the same response.
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        string pairingCode = await AdministrationJourneys.IssuePairingCodeAsync(administrator, tableIdentifier);

        // The tablet, in its own browser. Not hygiene: the display middleware ignores a device credential
        // on any request the Identity cookie already authenticated, so pairing inside the administrator's
        // browser would produce a screen that is the administrator and renders no code at all.
        IPage display = await instance.OpenIsolatedPageAsync();

        // §11.5's first rule, before anything is paired: an unpaired screen asking for a table display is
        // sent to the pairing surface rather than to a sign-in page a tablet could never satisfy.
        await display.GotoAsync(DisplayRoutes.ForTable(tableIdentifier));
        Assert.Equal("Pair this display", await HeadingAsync(display));

        Guid pairedTable = await DisplayJourneys.PairAsync(display, pairingCode, "E2E Window Tablet");

        // The code paired this device to the table it was issued for, and the surface is the table's own.
        Assert.Equal(tableIdentifier, pairedTable);
        Assert.Equal(tableLabel, await HeadingAsync(display));

        // Everything below waits on a *server timer* (§4.3), which only exists once a circuit does.
        // Prerendering alone renders a table label and a valid QR and then never moves again, so without
        // this the scenario's failure would be "the QR did not change" — true, and two steps from the
        // cause. See DisplayJourneys.WaitForLiveSurfaceAsync.
        await DisplayJourneys.WaitForLiveSurfaceAsync(display, InteractivityPatience);

        // §4.1 lets nothing render or return the join secret, so the row is the only place to learn what
        // the server is signing with — which is precisely what makes the next two assertions worth making
        // rather than tautological.
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        // Read the screen first, then the clock: the server rendered at or before the read, so the window
        // sampled afterwards is the newest one the screen could possibly be showing.
        string firstCode = await DisplayJourneys.ReadJoinQrPathAsync(display);
        AssertShowingLiveJoinCode(firstCode, joinSecret, tableIdentifier, instance);

        // §16.3 scenario 2's actual demand: the QR *changes across a window boundary*. Waiting for a
        // different path is waiting for §4.3's window-aligned refresh; the assertion that follows is what
        // separates "the screen redrew" from "the screen advanced to the next window's code".
        string secondCode = await DisplayJourneys.WaitForJoinQrPathAsync(
            display,
            candidate => !string.Equals(candidate, firstCode, StringComparison.Ordinal),
            RefreshPatience(instance.TableJoinTokenRotationSeconds),
            "a join code different from the one it started on",
            cancellationToken);

        Assert.NotEqual(firstCode, secondCode);
        AssertShowingLiveJoinCode(secondCode, joinSecret, tableIdentifier, instance);
    }

    // -------------------------------------------------------------------------------------------
    // 3. Guest scans (simulated URL from current token) → registers with a passkey, slowly enough that
    //    the token dies while they are doing it → joins on the grant alone; sitting created.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Guest_ScansRegistersWithPasskeyAndJoins()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Three";
        GuestAccount guestAccount = new("e2e.guest", "Hungry Guest");

        // The §13 floor, and the whole point of this scenario. §16.3 words it "(slowly — grant outlives
        // token)": the grant exists because registration can take longer than a rotation window, and at
        // ten seconds a window the token this guest scanned is provably dead — §4.3 accepts the current
        // and the previous window, so two windows and a moment — long before they finish. At the
        // default hour there would be nothing to outlive and the assertion would be vacuous.
        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(
                ShortestRotationSeconds, cancellationToken: cancellationToken);

        int rotationSeconds = instance.TableJoinTokenRotationSeconds;

        // Inserted rather than created through the administration surface, as scenario 14 does and for
        // the same reason: this scenario is about the guest's journey, and standing up an administrator
        // first would add a /setup wizard, an Argon2id hash and four form posts that nothing here
        // asserts on — while the clock this scenario is racing runs the whole time.
        byte[] joinSecret = RandomNumberGenerator.GetBytes(32);
        Guid tableIdentifier = await instance.InsertActiveTableAsync(tableLabel, joinSecret, cancellationToken);

        // The guest's own browser, with its own authenticator: a WebAuthn credential belongs to the
        // authenticator that minted it, so registering a passkey anywhere but here would produce one
        // this guest could never use.
        IPage guest = await instance.OpenIsolatedPageAsync(withVirtualAuthenticator: true);

        // (a) The scan. A live token, computed the way the display would have, on a table nobody is
        // sitting at yet. §4.4: the grant is written and an anonymous scanner goes to sign in.
        DateTimeOffset scannedAt = DateTimeOffset.UtcNow;
        string scannedToken = JoinTokenService.ComputeCurrentToken(
            joinSecret, tableIdentifier, scannedAt, rotationSeconds);

        TableJourneys.JoinStage afterScan =
            await TableJourneys.ScanAsync(guest, tableIdentifier, scannedToken);

        Assert.Equal(TableJourneys.JoinStage.SentToSignIn, afterScan);
        Assert.Contains(tableIdentifier.ToString("D"), guest.Url, StringComparison.Ordinal);

        // (b) Registration, from the sign-in page a first-time guest was just dropped on. No password:
        // the passkey is the only credential, which is §4.3's passkey-first default and the shape that
        // proves person.password_hash is genuinely optional (§3.2).
        await AccountJourneys.RegisterGuestWithPasskeyAsync(guest, guestAccount);

        // Registering returns them to the table — that is the return URL surviving two redirects and a
        // WebAuthn ceremony — and the grant, not the token, is what gets them past the door: the URL
        // they are on now carries no token at all.
        Assert.DoesNotContain("token=", guest.Url, StringComparison.Ordinal);
        Assert.Equal(TableJourneys.JoinStage.Confirm, await TableJourneys.JoinStageOnScreen(guest));

        // (c) "Slowly". Wait until the scanned token is past §4.3's current-and-previous acceptance, so
        // that what happens next cannot possibly be the token still working. Deliberately measured from
        // the instant it was minted rather than from now, because the registration above already spent
        // some of it.
        await WaitUntilTokenIsDeadAsync(scannedAt, rotationSeconds, cancellationToken);

        // Proven in a context of its own, so no grant cookie of the guest's can carry this navigation
        // past a refusal and quietly turn a failure into a pass.
        IPage bystander = await instance.OpenIsolatedPageAsync();
        Assert.Equal(
            TableJourneys.JoinStage.Expired,
            await TableJourneys.ScanAsync(bystander, tableIdentifier, scannedToken));

        // (d) And yet the guest joins, on a page that has been sitting on a dead token's table for
        // longer than that token lived. This is the grant doing the one job it exists for (§4.4:
        // "Registration mid-flow: the grant cookie survives the passkey ceremony; that is its purpose").
        await TableJourneys.JoinAsync(guest);

        Assert.Equal(TableJourneys.JoinStage.Member, await TableJourneys.JoinStageOnScreen(guest));
        Assert.Equal(tableLabel, await HeadingAsync(guest));

        // (e) "Sitting created" (§5.1) — one open sitting on this table, with this guest on it. The
        // page saying so is not quite the same claim: a second sitting the unique index should have
        // prevented would look identical from a seat.
        OpenSitting? sitting = await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(sitting);
        Assert.Equal(guestAccount.Username, Assert.Single(sitting!.MemberUsernames));
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
            await _harness.StartInstanceAsync(rotationSeconds, cancellationToken: cancellationToken);
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
    // 15. Admin rotates a table's join secret → in-flight token dies; display's next window works.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Fifteen";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(
                BoundaryWatchingRotationSeconds, cancellationToken: cancellationToken);

        // Read back rather than restated: every window computation below is against the value the
        // application was actually configured with, not the value it was asked for.
        int rotationSeconds = instance.TableJoinTokenRotationSeconds;

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        string pairingCode = await AdministrationJourneys.IssuePairingCodeAsync(administrator, tableIdentifier);

        IPage display = await instance.OpenIsolatedPageAsync();
        await DisplayJourneys.PairAsync(display, pairingCode, "E2E Rotation Tablet");

        // "The display's next window works" is a claim about a live circuit re-reading the secret. A
        // prerendered surface would satisfy every assertion up to the rotation and none after it.
        await DisplayJourneys.WaitForLiveSurfaceAsync(display, InteractivityPatience);

        byte[] originalSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);
        string codeBeforeRotation = await DisplayJourneys.ReadJoinQrPathAsync(display);
        AssertShowingLiveJoinCode(codeBeforeRotation, originalSecret, tableIdentifier, instance);

        // Exactly what a guest holding a freshly scanned code has: this window's token under the secret
        // in force right now. §4.1's promise — "immediately invalidates every outstanding QR" — is a
        // promise about this string and nothing else.
        string inFlightToken = JoinTokenService.ComputeCurrentToken(
            originalSecret, tableIdentifier, DateTimeOffset.UtcNow, rotationSeconds);

        await AdministrationJourneys.RotateJoinSecretAsync(administrator, tableIdentifier);

        byte[] rotatedSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);
        Assert.NotEqual(originalSecret, rotatedSecret);

        // (a) The in-flight token dies at once, not at the next boundary. Deliberately before the
        // acceptance half below, so that no grant cookie exists which could carry this browser past a
        // refusal and quietly turn a failure into a pass.
        IPage guest = await instance.OpenIsolatedPageAsync();
        await guest.GotoAsync(JoinPath(tableIdentifier, inFlightToken));

        Assert.Equal("That code has expired", await HeadingAsync(guest));
        Assert.DoesNotContain(AccountRoutes.SignIn, guest.Url);

        // (b) "The display's next window works": within one rotation the paired screen re-renders a code
        // the NEW secret signs, with nobody touching the tablet and nothing re-pairing it. The predicate
        // is the assertion here — waiting for merely *a different* path would also be satisfied by a
        // display that had drifted onto some other window of the old secret.
        string codeAfterRotation = await DisplayJourneys.WaitForJoinQrPathAsync(
            display,
            candidate => JoinQrCodes.IsLive(
                candidate,
                rotatedSecret,
                tableIdentifier,
                instance.PublicOrigin,
                DateTimeOffset.UtcNow,
                rotationSeconds),
            RefreshPatience(rotationSeconds),
            "a join code signed by the rotated secret",
            cancellationToken);

        Assert.NotEqual(codeBeforeRotation, codeAfterRotation);

        // ...and the new sequence is one a guest can actually use, which is what "works" has to mean.
        string freshToken = JoinTokenService.ComputeCurrentToken(
            rotatedSecret, tableIdentifier, DateTimeOffset.UtcNow, rotationSeconds);
        await guest.GotoAsync(JoinPath(tableIdentifier, freshToken));

        Assert.Contains(AccountRoutes.SignIn, guest.Url);
        Assert.Contains(tableIdentifier.ToString("D"), guest.Url);
    }

    // -------------------------------------------------------------------------------------------
    // 4. Guest stages 2 adds + note → Send → kitchen gets one loud alert → lines pending.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Guest_StagesAddsAndSend_KitchenGetsOneAlert()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Four";
        const string customizationNote = "No onions, extra hot";
        GuestAccount guestAccount = new("e2e.guest.four", "Four Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        // The surface under test belongs to the table the administrator created, rather than to some
        // other sitting this guest might hold — §5.1 allows several at once, and the URL is what scopes
        // the one on screen.
        Assert.Contains(service.TableIdentifier.ToString("D"), service.Guest.Url, StringComparison.Ordinal);

        // (a) Two adds and a note, staged. Nothing has reached the kitchen yet, and the board being
        // live and watching is what makes that an assertion rather than an assumption: §11.1 says the
        // basket is local until Send, and a surface that wrote as it staged would have alerted here.
        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1, customizationNote);
        await TableOrderJourneys.StageAsync(service.Guest, service.Pie, 2);

        Assert.Equal(2, await TableOrderJourneys.BasketLineCountAsync(service.Guest));

        KitchenBoardSnapshot beforeSend = await KitchenJourneys.ReadBoardAsync(service.Kitchen);
        Assert.Equal(0, beforeSend.UnseenAlertCount);
        Assert.Empty(beforeSend.PendingLines);

        // (b) Send. §6.5: one guest_submission owning both adds, priced inside the transaction.
        string confirmation = await TableOrderJourneys.SendAsync(service.Guest);

        Assert.Contains("2 items", confirmation, StringComparison.Ordinal);

        // (c) The board, waited for as one predicate over both facts rather than two waits in a row.
        // §9 publishes OrderLinesChanged before KitchenAlert and the board handles each as it arrives,
        // so there is a real window in which the queue has already re-read and the alert has not yet
        // been counted — a scenario that sampled it would report a silent kitchen.
        KitchenBoardSnapshot board = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 2 && snapshot.UnseenAlertCount >= 1,
            LiveUpdatePatience,
            "two lines on the pass and at least one unacknowledged alert",
            cancellationToken);

        // §16.3's word is "one". Two adds in one send is one order event and therefore one
        // kitchen_notification row and one alert (§10.1); a count of two would mean the alert had
        // become per-line, which is how a busy service turns into a siren nobody can hear over.
        Assert.Equal(1, board.UnseenAlertCount);

        // "Lines pending", in the kitchen's own terms: one row per order line, the quantities the guest
        // chose, and §11.2's prominent customization note against the line it belongs to.
        KitchenBoardLine soupOnThePass =
            Assert.Single(board.PendingLines, line => line.Name == service.Soup.Name);
        KitchenBoardLine pieOnThePass =
            Assert.Single(board.PendingLines, line => line.Name == service.Pie.Name);

        Assert.Equal(1, soupOnThePass.Quantity);
        Assert.Equal(customizationNote, soupOnThePass.Note);
        Assert.Equal(2, pieOnThePass.Quantity);
        Assert.Null(pieOnThePass.Note);

        // (d) The same two lines from the other end, and the basket emptied — §11.1 clears it only on
        // an accepted event, so a full basket here would mean the confirmation was about something else.
        IReadOnlyList<GuestOrderLine> guestLines = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(guestLines, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));
        Assert.Equal(0, await TableOrderJourneys.BasketLineCountAsync(service.Guest));

        // (e) Still one. Re-read now that both broadcasts have long since landed and the guest surface
        // has finished reacting to them, which turns "one alert so far" into "one alert, full stop".
        KitchenBoardSnapshot settled = await KitchenJourneys.ReadBoardAsync(service.Kitchen);

        Assert.Equal(1, settled.UnseenAlertCount);
    }

    // -------------------------------------------------------------------------------------------
    // 5. Second guest joins via fresh token → sees first guest's order live; first guest sees roster
    //    update.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task SecondGuest_JoinsAndSeesOrderLiveWithRosterUpdate()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Five";
        GuestAccount firstAccount = new("e2e.guest.five.one", "First Guest");
        GuestAccount secondAccount = new("e2e.guest.five.two", "Second Guest");

        // The default hour-long window. Unlike scenario 3 there is nothing here that wants a token to
        // die mid-run — "fresh" in §16.3's sentence means the code the table is showing at the moment
        // the second guest scans, not a code the first guest's has aged out of. Scenario 3 already owns
        // the expiry half, and a short window here would only add a clock to race.
        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        // Two items whose names are not substrings of one another, for the same reason scenarios 4 and
        // 6 insist on it: every assertion below finds a line by name.
        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        // (a) The first guest, seated and having ordered. Their send is what gives the second guest
        // something to see on arrival; without it "the rest of the table" would be empty and the
        // scenario would prove only that a roster grows.
        IPage first = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, firstAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(first, soup, 1);
        await TableOrderJourneys.SendAsync(first);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            first,
            lines => lines.Count == 1,
            LiveUpdatePatience,
            "the soup on the first guest's own order",
            cancellationToken);

        // Alone at the table. The "you" chip is asserted as well as the name because it is the only
        // thing that makes the roster this reader's view of the table rather than a list of strings —
        // and step (c) below turns on the distinction between it and everyone else.
        TableRosterMember aloneAtTheTable =
            Assert.Single(await TableOrderJourneys.ReadRosterAsync(first));

        Assert.Equal(firstAccount.DisplayName, aloneAtTheTable.Name);
        Assert.True(aloneAtTheTable.IsYou);

        // Nobody else has ordered, so §11.1's "the rest of the table" is empty. Asserted now so that
        // what the second guest's arrival changes is a change rather than a state it was born in.
        Assert.Empty(await TableOrderJourneys.ReadPartyAsync(first));

        // (b) The second guest, scanning the code the table is showing now — their own browser, their
        // own authenticator, their own account, and no knowledge of the first guest's session.
        IPage second = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, secondAccount, cancellationToken);

        // (c) "First guest sees roster update." Nobody touched the first guest's phone: TableJoin.razor
        // publishes SittingMemberJoined after the membership row commits (§9: "fired on: membership
        // insert"), the first guest's circuit re-reads, and the list grows. This is the assertion the
        // scenario exists for, and it is about a broadcast crossing between two circuits in two browser
        // contexts — not about anything either page did to itself.
        IReadOnlyList<TableRosterMember> roster = await TableOrderJourneys.WaitForRosterAsync(
            first,
            members => members.Count == 2,
            LiveUpdatePatience,
            "both guests on the roster",
            cancellationToken);

        Assert.Equal(firstAccount.DisplayName, Assert.Single(roster, member => member.IsYou).Name);
        Assert.Equal(secondAccount.DisplayName, Assert.Single(roster, member => !member.IsYou).Name);

        // The roster grew and the bill did not, which is the difference between §5.2's "who is here"
        // and §11.1's "the rest of the table". §6.1 creates a guest_order row lazily inside a first
        // send and sitting_bill is grouped from those rows, so a guest who has joined and ordered
        // nothing is on one list and absent from the other.
        Assert.Empty(await TableOrderJourneys.ReadPartyAsync(first));

        // (d) "Sees first guest's order." The second guest's surface was built by a circuit that never
        // saw the send — it started after it — so this comes from the read model rather than from a
        // notification, and it is the half that would still be true if §9 were switched off.
        IReadOnlyList<PartyOrder> onArrival = await TableOrderJourneys.WaitForPartyAsync(
            second,
            party => party.Count == 1 && party[0].Lines.Count == 1,
            LiveUpdatePatience,
            "the first guest's soup under the rest of the table",
            cancellationToken);

        PartyOrder theirOrderOnArrival = Assert.Single(onArrival);

        Assert.Equal(firstAccount.DisplayName, theirOrderOnArrival.BillName);
        Assert.Contains(
            soup.Name,
            Assert.Single(theirOrderOnArrival.Lines).Name,
            StringComparison.Ordinal);
        Assert.Equal(GuestLineBadge.WithTheKitchen, theirOrderOnArrival.Lines[0].Badge);

        // (e) "…live." The first guest orders again, on the other side of the restaurant, and nothing
        // touches the second guest's browser: no reload, no click, no navigation. §9 sends
        // OrderLinesChanged to the sitting's members and the second surface re-reads. That path is the
        // rest of what §16.3 scenario 5 is asking for, and it is the reason this waits rather than
        // reads.
        await TableOrderJourneys.StageAsync(first, pie, 2);
        await TableOrderJourneys.SendAsync(first);

        IReadOnlyList<PartyOrder> afterSecondSend = await TableOrderJourneys.WaitForPartyAsync(
            second,
            party => party.Count == 1 && party[0].Lines.Count == 2,
            LiveUpdatePatience,
            "both of the first guest's lines under the rest of the table",
            cancellationToken);

        PartyOrder theirGrownOrder = Assert.Single(afterSecondSend);

        Assert.Equal(firstAccount.DisplayName, theirGrownOrder.BillName);
        Assert.Single(theirGrownOrder.Lines, line => line.Name.Contains(soup.Name, StringComparison.Ordinal));

        // The quantity crossed too, not merely the name: §11.1 renders "2 × Steak pie", and a party
        // list that showed the line but lost the number would be a bill nobody could read.
        GuestOrderLine pieOnTheirOrder = Assert.Single(
            theirGrownOrder.Lines,
            line => line.Name.Contains(pie.Name, StringComparison.Ordinal));

        Assert.StartsWith("2 ", pieOnTheirOrder.Name, StringComparison.Ordinal);

        // (f) One sitting, two members, in join order (§5.1). The screens above cannot tell "both
        // joined the sitting" from "a second sitting was opened on the same table and the unique index
        // did not stop it" — from a seat those look identical, and only the rows say which happened.
        OpenSitting? sitting = await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(sitting);
        Assert.Equal(2, sitting!.MemberUsernames.Count);
        Assert.Equal(firstAccount.Username, sitting.MemberUsernames[0]);
        Assert.Equal(secondAccount.Username, sitting.MemberUsernames[1]);
    }

    // -------------------------------------------------------------------------------------------
    // 6. Kitchen fulfills one line → guest sees fulfilled badge.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Kitchen_FulfillsLine_GuestSeesFulfilledBadge()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Six";
        GuestAccount guestAccount = new("e2e.guest.six", "Six Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);
        await TableOrderJourneys.StageAsync(service.Guest, service.Pie, 1);
        await TableOrderJourneys.SendAsync(service.Guest);

        await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 2,
            LiveUpdatePatience,
            "both of the guest's lines on the pass",
            cancellationToken);

        // Both lines start with the kitchen on the guest's own screen. Asserted before the tap, so that
        // the badge below is a change rather than a state the surface happened to be born in.
        IReadOnlyList<GuestOrderLine> beforeFulfillment = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(beforeFulfillment, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        // §11.2: "tap a line → one fulfillment event". One line, not the whole ticket, because the half
        // of this scenario worth getting wrong is the other line staying where it was.
        await KitchenJourneys.FulfillLineAsync(service.Kitchen, service.Soup.Name);

        // Nobody touched the guest's phone. §9 sends LineFulfillmentChanged to the sitting's members and
        // the surface re-reads; that path — a second circuit, in a second browser context, reacting to a
        // commit in a first — is the whole of what this scenario proves.
        IReadOnlyList<GuestOrderLine> afterFulfillment = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Any(line => line.Badge == GuestLineBadge.AtYourTable),
            LiveUpdatePatience,
            "a line badged as at the table",
            cancellationToken);

        GuestOrderLine soupLine = Assert.Single(afterFulfillment,
            line => line.Name.Contains(service.Soup.Name, StringComparison.Ordinal));
        GuestOrderLine pieLine = Assert.Single(afterFulfillment,
            line => line.Name.Contains(service.Pie.Name, StringComparison.Ordinal));

        Assert.Equal(GuestLineBadge.AtYourTable, soupLine.Badge);
        Assert.Equal(GuestLineBadge.WithTheKitchen, pieLine.Badge);

        // And the pass is one line lighter — kitchen_pending_line excludes a fulfilled line (§8.3), so
        // the board losing it is the same fact from the writing side rather than a second opinion.
        KitchenBoardSnapshot board = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 1,
            LiveUpdatePatience,
            "one line left on the pass",
            cancellationToken);

        Assert.Equal(service.Pie.Name, Assert.Single(board.PendingLines).Name);
    }

    // -------------------------------------------------------------------------------------------
    // 7. Guest tries to remove the fulfilled line → whole batch rejected with per-op reason;
    //    removing their pending line succeeds.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Guest_RemoveFulfilledLineRejected_RemovePendingSucceeds()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Seven";
        GuestAccount guestAccount = new("e2e.guest.seven", "Seven Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        // (a) A soup and a pie, sent and pending. Two lines rather than one because the half of §6.5.9
        // worth proving is that the *good* operation in a refused batch does not slip through, and
        // that needs a second line for the batch to be all-or-nothing about.
        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);
        await TableOrderJourneys.StageAsync(service.Guest, service.Pie, 1);
        await TableOrderJourneys.SendAsync(service.Guest);

        await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 2,
            LiveUpdatePatience,
            "both of the guest's lines on the pass",
            cancellationToken);

        IReadOnlyList<GuestOrderLine> sent = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(sent, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        // (b) The guest ticks the soup while it is still theirs to take off (§6.5.3). Nothing is wrong
        // yet and nothing has been sent — this is where §16.3's "tries to" begins.
        Assert.True(await TableOrderJourneys.LineOffersRemovalAsync(service.Guest, service.Soup.Name));

        await TableOrderJourneys.MarkForRemovalAsync(service.Guest, service.Soup.Name);

        Assert.Equal(1, await TableOrderJourneys.BasketRemovalCountAsync(service.Guest));

        // (c) ...and then the kitchen passes that very plate, in the other browser, before the guest
        // has pressed anything.
        await KitchenJourneys.FulfillLineAsync(service.Kitchen, service.Soup.Name);

        // (d) The surface refuses on the guest's behalf. This is the honest browser answer to "guest
        // tries to remove the fulfilled line": §9's LineFulfillmentChanged reaches this circuit,
        // OrderStaging.PruneRemovals drops the mark that has stopped being valid, and §11.1 renders no
        // tick box on a fulfilled line at all. A guest cannot compose the refusable batch through this
        // surface — which is exactly why the refusal in (e) has to be reached another way, and why
        // that is a finding rather than a workaround.
        await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Any(line =>
                line.Name.Contains(service.Soup.Name, StringComparison.Ordinal)
                && line.Badge == GuestLineBadge.AtYourTable),
            LiveUpdatePatience,
            "the soup badged as at the table",
            cancellationToken);

        await TableOrderJourneys.WaitForBasketAsync(
            service.Guest,
            basket => basket is { StagedAdds: 0, TickedRemovals: 0 },
            LiveUpdatePatience,
            "the stale removal unticked",
            cancellationToken);

        // And the guest is told why the tick went. One commit raises two notifications —
        // OrderLinesChanged and then LineFulfillmentChanged (§9, IOrderWorkflow) — and this surface
        // re-reads on both, so this sentence used to be written by the first pass and erased by the
        // second before any human could read it. This assertion is what keeps it surviving.
        string? unticked = await TableOrderJourneys.ReadPruneNoticeAsync(service.Guest);

        Assert.NotNull(unticked);
        Assert.Contains("no longer yours to remove", unticked, StringComparison.Ordinal);

        // §6.5.3 as the difference between two rows on one screen rather than as a rule quoted at the
        // reader: gone from the line the kitchen passed, still there on the one it has not.
        Assert.False(await TableOrderJourneys.LineOffersRemovalAsync(service.Guest, service.Soup.Name));
        Assert.True(await TableOrderJourneys.LineOffersRemovalAsync(service.Guest, service.Pie.Name));

        // (e) "Whole batch rejected with per-op reason", reached the one way a guest still can. §7 is
        // explicit that a staged item which goes unavailable is *marked* and that the send
        // "re-validates server-side regardless" — the surface does not disarm the button, because the
        // transaction is the authority (§6.6). So: stage the soup again while it is on, let the
        // kitchen 86 it, and tick the pie for removal in the same basket. One add the transaction must
        // refuse, one removal that would have been perfectly fine on its own.
        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);

        await KitchenJourneys.EightySixAsync(service.Kitchen, service.Soup.Name);

        // Waited for rather than assumed. Sending before MenuChanged arrived would still be refused,
        // and for the right reason — but the scenario would then be about a guest who could not see it
        // coming, which is a different claim about §7 than the one it is making.
        await TableOrderJourneys.WaitForBasketAsync(
            service.Guest,
            basket => basket is { StagedAdds: 1, UnavailableMarks: 1 },
            LiveUpdatePatience,
            "the staged soup marked unavailable",
            cancellationToken);

        await TableOrderJourneys.MarkForRemovalAsync(service.Guest, service.Pie.Name);

        IReadOnlyList<string> refusal =
            await TableOrderJourneys.SendExpectingRefusalAsync(service.Guest);

        // One reason, naming the one operation §6.5.4 refused and why. The panel is keyed by
        // OrderMutationError.OperationIndex into the descriptions of the batch that was sent, so a
        // reason that named the wrong line would be a real defect rather than cosmetic.
        string only = Assert.Single(refusal);

        Assert.Contains(service.Soup.Name, only, StringComparison.Ordinal);
        Assert.Contains("currently unavailable", only, StringComparison.Ordinal);

        // "Nothing was sent", in the surface's own words: the basket is exactly as the guest left it,
        // both operations still in it.
        await TableOrderJourneys.WaitForBasketAsync(
            service.Guest,
            basket => basket is { StagedAdds: 1, TickedRemovals: 1 },
            LiveUpdatePatience,
            "the basket exactly as the guest left it",
            cancellationToken);

        // And all-or-nothing means the good half did not go either. The pie is still on the order and
        // still with the kitchen — a removal that had quietly committed while its batch was refused
        // would be the worst possible outcome of §6.5.9 and the hardest to notice.
        IReadOnlyList<GuestOrderLine> untouched =
            await TableOrderJourneys.ReadCommittedLinesAsync(service.Guest);

        Assert.Equal(2, untouched.Count);
        Assert.Equal(
            GuestLineBadge.WithTheKitchen,
            Assert.Single(untouched, line => line.Name.Contains(service.Pie.Name, StringComparison.Ordinal))
                .Badge);

        // The kitchen's copy agrees, which is the same fact from the other side of the pass.
        KitchenBoardSnapshot stillWaiting = await KitchenJourneys.ReadBoardAsync(service.Kitchen);

        Assert.Equal(service.Pie.Name, Assert.Single(stillWaiting.PendingLines).Name);

        // (f) "Removing their pending line succeeds." Take the 86'd soup out and send again with
        // nothing in the basket but the tick. Same line, same guest, same tick — the only thing that
        // changed is that the batch no longer carries an operation the transaction must refuse, which
        // is precisely what "all-or-nothing" is supposed to mean.
        await TableOrderJourneys.UnstageAsync(service.Guest, service.Soup.Name);

        string confirmation = await TableOrderJourneys.SendAsync(service.Guest);

        Assert.Contains("taken off", confirmation, StringComparison.Ordinal);

        IReadOnlyList<GuestOrderLine> afterRemoval = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Any(line =>
                line.Name.Contains(service.Pie.Name, StringComparison.Ordinal)
                && line.Badge == GuestLineBadge.Removed),
            LiveUpdatePatience,
            "the pie struck through as removed",
            cancellationToken);

        // §11.1 keeps a removed line on screen — "removed lines struck-through with actor + reason" —
        // so the count does not fall. That is the whole difference between a line a guest took off and
        // a line that was never ordered, and §6.8's history depends on it.
        Assert.Equal(2, afterRemoval.Count);
        Assert.Equal(
            GuestLineBadge.AtYourTable,
            Assert.Single(afterRemoval, line => line.Name.Contains(service.Soup.Name, StringComparison.Ordinal))
                .Badge);

        // And the pass empties: order_current_line drops a removed line in SQL (§8.3), so the kitchen
        // is not left holding a plate nobody is going to eat.
        await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 0,
            LiveUpdatePatience,
            "nothing left on the pass",
            cancellationToken);
    }

    // -------------------------------------------------------------------------------------------
    // 8. A send sits unfulfilled past the reminder threshold → exactly one reminder alert.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Send_UnfulfilledPastThreshold_YieldsExactlyOneReminder()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Eight";
        GuestAccount guestAccount = new("e2e.guest.eight", "Eight Guest");

        // The only scenario that moves this dial. §16.3 says sixty seconds and means "long enough that
        // a kitchen has plainly ignored it"; the rule under test is the §8.4 scan, and the scan does
        // not know or care what the threshold is set to. Five seconds buys the same rule for a tenth of
        // the wall clock. See RestaurantInstance.DefaultKitchenSubmissionReminderSeconds for why every
        // other scenario deliberately leaves it at sixty.
        await using RestaurantInstance instance = await _harness.StartInstanceAsync(
            kitchenSubmissionReminderSeconds: ImpatientReminderSeconds,
            cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        // (a) One line, sent, and then nobody in the kitchen does a thing. One rather than two because
        // §8.4 reminds per *send* — the reminder is about the ticket having been ignored, not about how
        // much was on it — and a second line would only give the scenario something else to explain.
        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);
        await TableOrderJourneys.SendAsync(service.Guest);

        KitchenBoardSnapshot afterSend = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 1 && snapshot.UnseenAlertCount == 1,
            LiveUpdatePatience,
            "the sent line on the pass under §10.1's single initial alert",
            cancellationToken);

        // The alert on the badge right now is the send's own. Asserted before the wait below so that
        // the reminder, when it lands, is a second thing arriving rather than the first thing being
        // reinterpreted — without this, a board that had somehow alerted twice at the send would
        // satisfy every remaining assertion in this scenario.
        Assert.Equal(0, afterSend.UnseenReminderCount);
        Assert.Equal(service.Soup.Name, Assert.Single(afterSend.PendingLines).Name);

        OpenSitting? sitting =
            await instance.ReadOpenSittingAsync(service.TableIdentifier, cancellationToken);

        Assert.NotNull(sitting);

        // §10.1's row, written inside the send's own transaction. The reminder's absence here is the
        // half of §8.4 that says a reminder is not merely a second copy of the initial alert: nothing
        // has written one, because nothing has yet been ignored for long enough.
        Assert.Equal(
            new KitchenNotificationTally(Initial: 1, Reminder: 0),
            await instance.ReadKitchenNotificationsAsync(sitting!.SittingIdentifier, cancellationToken));

        // (b) The threshold passes, and the board alerts a second time with nobody having touched
        // anything anywhere. This is the one event in the whole matrix that no browser causes: §10.2's
        // background service scans every five seconds, finds a guest submission older than the
        // threshold with nothing fulfilled or removed off it, writes one row, and broadcasts.
        KitchenBoardSnapshot reminded = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.UnseenReminderCount >= 1,
            ReminderPatience(instance.KitchenSubmissionReminderSeconds),
            "the overdue send's reminder counted on the badge",
            cancellationToken);

        // Two alerts, one of them overdue — not two overdue, and not one alert that changed its mind.
        // §10.3 keeps the reminder count as a subset of the unseen count precisely so the badge can say
        // "2 new alerts (1 overdue)", and both halves of that sentence are load-bearing to a cook
        // deciding what to pick up next.
        Assert.Equal(1, reminded.UnseenReminderCount);
        Assert.Equal(2, reminded.UnseenAlertCount);

        // And the line is still exactly where it was. A reminder is a nudge, not a mutation: it must
        // not touch kitchen_pending_line, and a board that quietly dropped the ticket it was reminding
        // about would be the worst possible reading of §10.2.
        Assert.Equal(service.Soup.Name, Assert.Single(reminded.PendingLines).Name);

        Assert.Equal(
            new KitchenNotificationTally(Initial: 1, Reminder: 1),
            await instance.ReadKitchenNotificationsAsync(sitting.SittingIdentifier, cancellationToken));

        // (c) "Exactly one." The count on the badge cannot carry that word on its own — it only ever
        // rises, so two is two whether the second arrived a second ago or a minute ago. Clearing it
        // first (§10.3's "tap to clear") turns any further alert into a rise from zero, which is a
        // thing that can be watched for rather than inferred.
        await KitchenJourneys.AcknowledgeAlertsAsync(service.Kitchen);

        // Three §8.4 scans go by with the send still sitting there, still overdue, still matching every
        // clause of the query except the one that matters: the NOT EXISTS on a prior reminder row. The
        // returned counts are the high-water mark over the whole stretch, not a reading at the end, so
        // an alert that arrived and was somehow cleared inside the window still fails this.
        KitchenBoardSnapshot quiet = await KitchenJourneys.WatchBoardAsync(
            service.Kitchen, QuietWatch, cancellationToken);

        Assert.Equal(0, quiet.UnseenReminderCount);
        Assert.Equal(0, quiet.UnseenAlertCount);
        Assert.Equal(service.Soup.Name, Assert.Single(quiet.PendingLines).Name);

        // The rows say it too, and they are the ones that actually enforce it: UNIQUE
        // (order_event_identifier, kind) is what makes a reminder singular, and §8.4's RETURNING is how
        // the scan learns its insert was swallowed and declines to broadcast. A quiet board with two
        // reminder rows behind it would mean the constraint had gone and the silence was luck.
        Assert.Equal(
            new KitchenNotificationTally(Initial: 1, Reminder: 1),
            await instance.ReadKitchenNotificationsAsync(sitting.SittingIdentifier, cancellationToken));
    }

    // -------------------------------------------------------------------------------------------
    // Still to come. Each is one required §16.3 scenario, named so the matrix stays legible.
    // -------------------------------------------------------------------------------------------

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

    /// <summary>
    /// How long to wait for §4.3's window-aligned refresh. Two full rotations plus slack: one window
    /// because the display fires at the <em>next</em> boundary and the wait may have started just after
    /// the last one, a second because a container under load can lose a boundary, and twenty seconds
    /// because a timeout that fires while the thing was about to happen is the worst kind of flake.
    /// </summary>
    private static TimeSpan RefreshPatience(int rotationSeconds)
        => TimeSpan.FromSeconds((rotationSeconds * 2) + 20);

    /// <summary>
    /// How long to give §10.2's reminder to arrive after a send goes untouched.
    ///
    /// <para>The threshold itself, plus two scan intervals, plus twenty seconds. Two intervals because
    /// the scan is a <c>PeriodicTimer</c> started with the process rather than with the send, so a send
    /// landing an instant after a tick waits a whole extra interval before it is even looked at — and a
    /// second interval because the tick that finally sees it may be the one a busy container skipped.
    /// The twenty seconds are the same slack every other wait here carries, and are why a timeout at
    /// this length means the reminder service is not running rather than that it is running late.</para>
    /// </summary>
    private static TimeSpan ReminderPatience(int reminderSeconds)
        => TimeSpan.FromSeconds(reminderSeconds)
            + (KitchenReminderService.ScanInterval * 2)
            + TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to watch a cleared board before calling a reminder singular.
    ///
    /// <para>Three §8.4 scans. One would be enough to catch the obvious regression — the guard clause
    /// dropped, the constraint gone — but a scenario that watched exactly one tick would be asserting
    /// on whether the timer had woken up at all, and would pass just as happily against a reminder
    /// service that had died. Three is short enough to cost fifteen seconds and long enough that the
    /// loop has demonstrably run and demonstrably declined.</para>
    /// </summary>
    private static readonly TimeSpan QuietWatch = KitchenReminderService.ScanInterval * 3;

    /// <summary>
    /// How long to give a page to become interactive. Independent of the rotation window, because this is
    /// a WebSocket handshake and one render batch rather than anything on a timer — thirty seconds is the
    /// same patience <see cref="RestaurantInstance"/> gives every other page operation, and a circuit that
    /// has not arrived by then is not late, it is absent.
    /// </summary>
    private static readonly TimeSpan InteractivityPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for a §9 broadcast to reach another circuit and repaint it. The broadcaster is
    /// in-process and the queries behind each re-read are small and indexed, so the honest expectation is
    /// milliseconds; thirty seconds is the same patience every other page operation gets, and is here to
    /// absorb a container that is busy compiling somebody else's scenario rather than to allow for
    /// anything this code does. A timeout at this length means the notification did not arrive at all.
    /// </summary>
    private static readonly TimeSpan LiveUpdatePatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A restaurant mid-service: an administrator standing at the kitchen board, a guest seated at a
    /// table with a live ordering surface, and two things on the menu to order.
    /// </summary>
    private sealed record ArrangedService(
        IPage Kitchen,
        IPage Guest,
        Guid TableIdentifier,
        MenuItemOnTheMenu Soup,
        MenuItemOnTheMenu Pie);

    /// <summary>
    /// Everything §16.3 scenarios 4 and 6 need before their first interesting line, arranged the way a
    /// restaurant would arrange it: an administrator bootstraps, puts two things on the menu and creates
    /// a table; a guest scans it, registers, and joins; the kitchen board is opened and becomes live.
    ///
    /// <para><b>The kitchen board is the administrator's own browser, and that is deliberate.</b> §3.7
    /// admits both <c>kitchen</c> and <c>administrator</c> to <c>/kitchen</c>, and an administrator
    /// covering the pass is a real thing the application supports — <c>KitchenBoard.razor</c> goes out of
    /// its way to record them as themselves rather than as the kitchen. Standing up a separate kitchen
    /// account would mean <c>/administration/people/new</c>, a forced password change (§3.2), and a
    /// second sign-in, none of which either scenario asserts on. Scenarios that are <em>about</em> a
    /// staff account's own journey will create one.</para>
    ///
    /// <para><b>The board is opened before anything is sent.</b> An alert is a §9 broadcast to
    /// subscribers and <c>KitchenBoard.razor</c> subscribes on its first interactive render. A board
    /// opened after the send would show the queue perfectly — that comes from <c>kitchen_pending_line</c>
    /// — and would have heard nothing, which is precisely the half of §10 these scenarios exist for.</para>
    /// </summary>
    private static async Task<ArrangedService> ArrangeServiceAsync(
        RestaurantInstance instance,
        string tableLabel,
        GuestAccount guestAccount,
        CancellationToken cancellationToken)
    {
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        // Two items, with names that are not substrings of one another: every assertion below matches a
        // line by name, and "Soup" inside "Soup of the day" would make one of them meaningless.
        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        await KitchenJourneys.OpenAsync(administrator, InteractivityPatience);

        return new ArrangedService(administrator, guest, tableIdentifier, soup, pie);
    }

    /// <summary>
    /// One guest, from a code on a table to a live ordering surface: scan, self-register with a passkey,
    /// join, and wait for the circuit. Returns their page.
    ///
    /// <para><b>Their own browser context, with its own authenticator.</b> Cookies are per-context, and a
    /// WebAuthn credential belongs to the authenticator that minted it — a passkey created anywhere else
    /// would be offered to the wrong person and to nobody useful. §16.3 scenario 5 needs two of these
    /// alive at once, which is the reason this is a method rather than four lines inside one
    /// arrangement.</para>
    ///
    /// <para><b>The token is computed at the moment of the scan</b>, from the secret read out of the row
    /// (<see cref="RestaurantInstance.ReadJoinSecretAsync"/>) rather than decoded off a display: these
    /// scenarios are about what happens after the guest is seated, and pairing a tablet to get at a QR
    /// would put scenario 2's whole apparatus in front of them. The token is still one the server really
    /// verifies, and a second guest arriving later gets the code the table is showing then rather than a
    /// copy of the first guest's.</para>
    /// </summary>
    private static async Task<IPage> SeatGuestAsync(
        RestaurantInstance instance,
        Guid tableIdentifier,
        byte[] joinSecret,
        GuestAccount account,
        CancellationToken cancellationToken)
    {
        string token = JoinTokenService.ComputeCurrentToken(
            joinSecret, tableIdentifier, DateTimeOffset.UtcNow, instance.TableJoinTokenRotationSeconds);

        IPage guest = await instance.OpenIsolatedPageAsync(withVirtualAuthenticator: true);

        Assert.Equal(
            TableJourneys.JoinStage.SentToSignIn,
            await TableJourneys.ScanAsync(guest, tableIdentifier, token));

        await AccountJourneys.RegisterGuestWithPasskeyAsync(guest, account);
        await TableJourneys.JoinAsync(guest);
        await TableOrderJourneys.WaitForLiveSurfaceAsync(guest, InteractivityPatience);

        // The cancellation token is not idle: it is the scenario's, and every wait above is bounded by
        // its own timeout rather than by cancellation. Observing it here means a cancelled run stops at
        // the seam between guests instead of registering a second account nobody will look at.
        cancellationToken.ThrowIfCancellationRequested();

        return guest;
    }

    /// <summary>
    /// Waits until a token minted at <paramref name="mintedAt"/> is outside §4.3's acceptance window.
    ///
    /// <para>The arithmetic, spelled out because an off-by-one here would make the assertion that
    /// follows it meaningless rather than merely wrong: a token is accepted while its window index is
    /// the current one or the previous one, so the last instant it can work is the end of the window
    /// after the one it was minted in. Waiting to <c>(mint window + 2) × rotation</c> reaches exactly
    /// that boundary, and a second of slack carries it past.</para>
    /// </summary>
    private static async Task WaitUntilTokenIsDeadAsync(
        DateTimeOffset mintedAt,
        int rotationSeconds,
        CancellationToken cancellationToken)
    {
        long mintedWindow = JoinTokenService.CurrentWindowIndex(mintedAt, rotationSeconds);
        DateTimeOffset deadAt = DateTimeOffset
            .FromUnixTimeSeconds((mintedWindow + 2) * rotationSeconds)
            .AddSeconds(1);

        TimeSpan remaining = deadAt - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    /// <summary>
    /// Asserts that the QR on screen is the code this table's secret produces for the current or the
    /// previous window — §4.3's definition of one that still validates.
    ///
    /// <para>The comparison is deliberately reported as a phrase rather than as two thousand characters of
    /// SVG path: a display frozen on a stale code and a display showing a code from another table both
    /// fail this, and which of the two it was is the entire content of the failure.</para>
    /// </summary>
    private static void AssertShowingLiveJoinCode(
        string observedQrPath,
        byte[] joinSecret,
        Guid tableIdentifier,
        RestaurantInstance instance)
    {
        long newestWindowIndex = JoinTokenService.CurrentWindowIndex(
            DateTimeOffset.UtcNow, instance.TableJoinTokenRotationSeconds);

        string age = JoinQrCodes.Classify(
            observedQrPath,
            joinSecret,
            tableIdentifier,
            instance.PublicOrigin,
            newestWindowIndex);

        Assert.Contains(age, JoinQrCodes.LiveAges);
    }
}
