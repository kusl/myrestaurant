using System.Globalization;
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
/// <para><b>The matrix closed at fifteen in M6 Slice 15, and there are no placeholders left; a sixteenth
/// was appended in Slice 32.</b> M6 Slice 2
/// brought <b>1</b> (the first-administrator bootstrap, including a real
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
/// one here. M6 Slice 12 adds <b>9</b> (a counter adjusts a unit price with a reason and the guest's own
/// screen reads old → new), the first driven by a <em>staff</em> account rather than by the
/// administrator standing in for one — which means the first to walk §3.7's create-staff form and §3.5's
/// forced password change on the way to the thing under test, and the only one where who acted is itself
/// part of the assertion. M6 Slice 13 adds <b>10</b> (a counter is warned about a line still on the pass,
/// settles anyway, and the table becomes read-only on three screens at once while seven independent
/// computations of one total agree) — the first whose subject is a write that cannot be undone, and the
/// only one that asserts a number against the column it was stamped into rather than against another
/// rendering of the same query. M6 Slice 14 adds <b>11</b> (a guest hides a settled order, it leaves their
/// own history while the till's bill and another guest's history are untouched, and an administrator
/// finds it by username and puts it back) — the first scenario whose subject is a record that is
/// <em>still there</em>, and therefore the first whose central assertion is about what did
/// <em>not</em> change. M6 Slice 15 closes the matrix with <b>12</b> (an administrator resets a
/// TOTP-enrolled account, and it is held by §3.5's pipeline on both credentials, clears both obligations
/// in order, and lands where it was headed) — the first whose subject is authentication itself rather
/// than something reached through it, the only one that drives one person through two browsers because a
/// WebAuthn key cannot be in both, and the only one that asserts a mechanism is <em>released</em> as well
/// as that it fires.</para>
///
/// <para><b>M6 Slice 32 appends <b>16</b>, and it is the first scenario in this matrix whose subject is
/// not a flow.</b> An administrator works §11.4's administration surfaces from a 375×667 handset, and the
/// assertions are three numbers a browser computes: no page is wider than its own viewport, every row's
/// action lies inside it, every control is 44px tall. Nothing in this project had ever asserted anything
/// about layout at any width, which is exactly why F-59 survived four milestones with every gate green —
/// and <c>HandheldLayoutContractTests</c>, added with the fix, asserts the *structure* of §11.12 and by
/// construction cannot decide whether a control is on the screen. This is the assertion F-59 would have
/// failed. Appended as sixteen rather than inserted, because the harness and this file name scenarios by
/// number in a great many places. It walked four surfaces when it was written, six once Slice 33 converted
/// the two explorers, and ten since Slice 34 finished the indexes and added the detail surfaces beside
/// them — see <see cref="HandheldAdministrationIndexPaths"/> and <see cref="HandheldDetailPaths"/>.
/// Measuring a page is what converting one costs.</para>
///
/// <para>Every scenario begins with <see cref="SkipUnlessHarnessAvailable"/>. The scenarios are opt-in
/// (<c>MYRESTAURANT_E2E=1</c>) and additionally need a container engine, a Chromium build and a
/// current build of the web application; each absence is a skip with the fix in its message. See
/// <see cref="RestaurantHarness"/>.</para>
/// </summary>
public sealed class EndToEndScenarios : IClassFixture<RestaurantHarness>
{
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

    /// <summary>
    /// The password §16.3 scenario 9's counter chooses when §3.5 forces them off the temporary one. A
    /// passphrase rather than a short scramble because §3.2 requires twelve characters and refuses
    /// nothing else — and because a fixture that failed the policy would fail inside a validation list
    /// on the forced-change page, which is a tedious thing to diagnose from a timeout two steps later.
    /// </summary>
    private const string CounterPassword = "settle up at the till please";

    /// <summary>
    /// The unit price §16.3 scenario 9's counter adjusts the pie to. Three below its menu price of
    /// fourteen, on a line of two, so that the extension has to move by six — a difference no rounding
    /// and no coincidence produces, and one that cannot be confused with the unit price itself.
    /// </summary>
    private const decimal AdjustedPieUnitPrice = 11.00m;

    /// <summary>
    /// §6.5.7 requires a reason and the <c>order_event</c> CHECK enforces it, so this is the one
    /// mutation in the system that cannot be made silently. Worded like something a person would
    /// actually type, because §11.1 shows it to the guest verbatim.
    /// </summary>
    private const string PriceAdjustmentReason = "Small bowl, agreed at the till";

    /// <summary>
    /// How many pies §16.3 scenario 9's guest orders. Two, and the number is the whole reason the
    /// scenario can tell one claim from another: §6.5.7 adjusts a <em>unit</em> price and §11.1 renders
    /// the extension, so at any quantity above one "the adjustment landed" and "the bill was
    /// recalculated" are separable observations. At one they are the same number twice.
    /// </summary>
    private const int AdjustedLineQuantity = 2;

    /// <summary>
    /// The password §16.3 scenario 10's counter chooses when §3.5 forces them off the temporary one. A
    /// passphrase of its own rather than scenario 9's, because each scenario builds its own restaurant and
    /// a shared fixture between two of them would be a fact neither one states.
    /// </summary>
    private const string ClosingCounterPassword = "close the table and cash up";

    /// <summary>
    /// How many pies §16.3 scenario 10's guest orders — the line the kitchen never delivers and the table
    /// is charged for anyway (§5.3's "knowingly charge").
    ///
    /// <para>Two, and for a different reason from <see cref="AdjustedLineQuantity"/>'s. The settled total
    /// has to be a number that could not have arisen from a simpler arithmetic mistake: at one of each
    /// item, a total that summed the <em>unit</em> prices instead of the extensions would be identical to
    /// the correct one, and so would a total that counted the delivered line twice. At one soup and two
    /// pies every wrong sum is a different number from the right one.</para>
    /// </summary>
    private const int UndeliveredLineQuantity = 2;

    /// <summary>
    /// How many pies §16.3 scenario 11's hider orders, beside one soup.
    ///
    /// <para>Three, and the number's only job is to make four amounts on four screens mutually
    /// distinguishable. The hider's own share is <c>soup + 3 × pie</c>, the bystander's is one soup, and
    /// the table's stamped total is their sum — so the figure §11.1's history page shows the hider cannot
    /// be confused with the table's total, with the other guest's, or with any unit price. A hide that
    /// quietly narrowed a bill, or a history that quietly showed the table's figure instead of the
    /// person's, would be a different number here rather than the same one.</para>
    /// </summary>
    private const int HiddenOrderPieQuantity = 3;

    /// <summary>How many lines §16.3 scenario 11's hider ends up with: the soup and the pies.</summary>
    private const int HiddenOrderLineCount = 2;

    /// <summary>
    /// The password §16.3 scenario 12's kitchen account chooses the first time §3.5 forces it off a
    /// temporary one — before the administrator resets it again.
    ///
    /// <para>Twelve characters and nothing else, per §3.2. Distinct from
    /// <see cref="ReenrolledKitchenPassword"/> and that is the whole reason there are two: the scenario
    /// walks §3.5's obligation (1) twice, and a single shared passphrase would let a forced-change page
    /// that had accepted the <em>wrong</em> current password pass both times.</para>
    /// </summary>
    private const string FirstKitchenPassword = "hot pass and a clean board";

    /// <summary>
    /// The password §16.3 scenario 12's kitchen account chooses on the far side of the administrative
    /// reset — the one it holds when it finally lands home.
    /// </summary>
    private const string ReenrolledKitchenPassword = "a new pass for a new week";

    /// <summary>
    /// The §11.4 administration <em>index</em> surfaces §16.3 scenario 16 measures, in the order the area
    /// links render them.
    ///
    /// <para>Four when the barrier was built — the four Slice 30 restructured — and six since Slice 33
    /// converted the two explorers. Slice 34 finished the indexes and added the <em>detail</em> surfaces
    /// beside them; those are not in this array because their routes carry an identifier, so they are
    /// built from what the scenario minted (see <see cref="HandheldDetailPaths"/>). Ten surfaces
    /// altogether.</para>
    ///
    /// <para><c>/administration/hidden-records</c> is measured <b>empty</b>, and that is stated rather
    /// than glossed. Putting a row on it needs a guest, a token, a join, an order and a close before
    /// anything can be hidden — which is scenario 11's subject and four scenarios' worth of arrangement.
    /// The page still has to lay out, its filter is the same §11.12 vocabulary the event explorer renders,
    /// and its submit button is measured; what is untested there is a <em>record card</em> on that page,
    /// not the page. Sittings has been measured on the same terms since the barrier was written.</para>
    /// </summary>
    private static readonly string[] HandheldAdministrationIndexPaths =
    [
        "/administration",
        "/administration/tables",
        "/administration/menu",
        "/administration/sittings",
        "/administration/hidden-records",
        "/administration/events",
    ];

    /// <summary>
    /// The §11.4 detail surfaces the same scenario measures: one account, one table, that table's
    /// displays, one menu item.
    ///
    /// <para><b>Why these four and not five.</b> They are the four whose identifier this scenario already
    /// holds — an account and a table and an item it created, and a table's display roster reached from
    /// the table. Every one of them was carrying its own inline copy of the detail vocabulary until Slice
    /// 34, with form controls 34px tall and no font floor, and none of them had ever been laid out at
    /// 375px by anything (F-66). Converting a page and not measuring it is how F-59 survived four
    /// milestones.</para>
    ///
    /// <para><c>/administration/sittings/{sitting}</c> is the fifth, is converted in the same slice, and
    /// is deliberately <b>not</b> here. Reaching a sitting needs a guest, a table token and a join before
    /// there is an identifier to put in the route — scenario 3's arrangement, three scenarios' worth of
    /// setup for one measurement — and a barrier that navigated to a made-up identifier would meet the
    /// not-found panel, which has no page head and would fail on arrival rather than measure anything.
    /// So that page's conversion rests on the contract test and on reading app.css, and this sentence is
    /// the record of it. It is the same honest gap <c>/administration/hidden-records</c> is measured
    /// with, one route deeper.</para>
    /// </summary>
    private static string[] HandheldDetailPaths(Guid personIdentifier, Guid tableIdentifier, Guid menuItemIdentifier)
        =>
        [
            string.Create(CultureInfo.InvariantCulture, $"/administration/people/{personIdentifier:D}"),
            string.Create(CultureInfo.InvariantCulture, $"/administration/tables/{tableIdentifier:D}"),
            string.Create(CultureInfo.InvariantCulture, $"/administration/tables/{tableIdentifier:D}/displays"),
            string.Create(CultureInfo.InvariantCulture, $"/administration/menu/{menuItemIdentifier:D}"),
        ];

    /// <summary>
    /// How much narrower than the configured viewport <c>document.documentElement.clientWidth</c> is
    /// allowed to read. It excludes a classic scrollbar, headless Chromium draws one on any page that
    /// scrolls vertically, and every administration index does — so the measured width is legitimately a
    /// dozen-odd pixels under 375. Twenty is comfortably over any scrollbar and nowhere near a viewport
    /// this scenario would accept as a handset.
    /// </summary>
    private const double ScrollbarAllowancePixels = 20.0;

    /// <summary>
    /// The floor on how many controls §16.3 scenario 16 must have measured before its verdicts mean
    /// anything.
    ///
    /// <para><b>No expected total is stated beside it, and that is F-91.</b> The sentence that used to
    /// stand here said <em>fifteen are expected since Slice 34</em>, and the comment it pointed at
    /// itemised a census that had been wrong since Slice 38 added a third form to <c>ManageMenuItem</c> —
    /// a count of rendered controls is a fact about ten surfaces that nothing in this tree can check, and
    /// it went stale in silence because a floor that passes at fifteen also passes at seventeen (F-77).
    /// The floor is set under the smallest selector group rather than under a total: <c>.filter-actions</c>
    /// contributes two controls, one per read-only explorer, so that is the smallest disappearance this
    /// number has to survive and still catch. The comment at the assertion carries the rule and the
    /// residual.</para>
    /// </summary>
    private const int MinimumControlsMeasured = 14;

    /// <summary>The counter account §16.3 scenario 16 creates so that the people index has two rows.</summary>
    private const string HandheldCounterUsername = "e2e.sixteen.counter";

    /// <summary>
    /// That account's display name, and the unbroken run in it is the point rather than a joke.
    ///
    /// <para>A single token longer than the card is wide is the one input that can push a record card
    /// past the viewport, and §11.12 relies on <c>overflow-wrap: anywhere</c> to stop it. The choice of
    /// keyword is load-bearing: <c>break-word</c> breaks the line but leaves the element's
    /// <em>min-content</em> width at the length of the token, so a flex or table context still sizes to
    /// it and the page still scrolls sideways. Only <c>anywhere</c> shrinks min-content. A name with no
    /// break opportunity in it is therefore the difference between asserting that the stylesheet says
    /// the right word and asserting that it does the right thing.</para>
    /// </summary>
    private const string HandheldCounterDisplayName = "Anastasia Featherstonehaughwolstenholmeworthington";

    /// <summary>The table §16.3 scenario 16 creates so that the tables index has a row.</summary>
    private const string HandheldTableLabel = "E2E Sixteen";

    /// <summary>The item §16.3 scenario 16 puts on the menu so that the menu index has a row.</summary>
    private const string HandheldMenuItemName = "Handheld Soup";

    /// <summary>Its price. Nothing asserts on it; it exists because §7's form requires one.</summary>
    private const decimal HandheldMenuItemPrice = 6.50m;

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
    // 9. Counter adjusts a price with reason → guest sees old → new with reason.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Counter_AdjustsPriceWithReason_GuestSeesOldToNew()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Nine";
        GuestAccount guestAccount = new("e2e.guest.nine", "Nine Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        // (a) An administrator, two things on the menu, a table — and a counter account, which is what
        // makes this scenario different from every one before it. Scenarios 4, 6, 7 and 8 deliberately
        // put an administrator at the pass rather than stand up a staff account (§3.7 admits both, and
        // an administrator covering a station is a real thing the application supports). This one
        // cannot: the sentence under test names who changed the price, and §6.2 records counter and
        // administrator as different actors. An administrator adjusting the price would produce "by an
        // administrator" and the assertion would be about the wrong role.
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        // Created before the guest is seated, so §8.4's clock — which starts at the send — spends none
        // of itself on an Argon2id hash and four form posts. Nothing here asserts on a reminder, and at
        // the default sixty seconds one could not arrive anyway; it is simply the right order.
        StaffAccount counterAccount = await AdministrationJourneys.CreateStaffAccountAsync(
            administrator, "e2e.counter.nine", "Nine Counter", StaffRoles.Counter);

        Assert.NotEqual(string.Empty, counterAccount.TemporaryPassword);

        // The two figures the till must show, derived from the prices this scenario actually put on the
        // menu rather than restated as constants. A restated total is a second place the fixture lives,
        // and the day somebody adjusts the soup's price to make another scenario read better, a restated
        // total goes quietly wrong while every assertion still passes for the wrong reason.
        decimal unadjustedTableTotal = soup.PriceAmount + (AdjustedLineQuantity * pie.PriceAmount);
        decimal adjustedTableTotal = soup.PriceAmount + (AdjustedLineQuantity * AdjustedPieUnitPrice);

        // (b) The guest, seated, with two lines on the pass. Two quantities rather than two of the same,
        // because the pie is the one about to be adjusted and its quantity is what makes the adjustment
        // legible: a unit price that moves by three on a line of two has to move the money by six. At
        // quantity one, "the unit price changed" and "the extension was recomputed" are the same
        // observation and the weaker of the two claims would pass for both.
        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(guest, soup, 1);
        await TableOrderJourneys.StageAsync(guest, pie, AdjustedLineQuantity);
        await TableOrderJourneys.SendAsync(guest);

        IReadOnlyList<GuestOrderLine> sent = await TableOrderJourneys.WaitForCommittedLinesAsync(
            guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(sent, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        // The pie at its menu price, asserted before anything moves it. Without this the assertion in
        // (f) would be about a number the surface might always have been showing.
        GuestOrderLineDetail beforeAdjustment = await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            pie.Name,
            line => line.PriceAdjustments.Count == 0,
            LiveUpdatePatience,
            "the pie at its unadjusted menu price",
            cancellationToken);

        Assert.Equal(Money(pie.PriceAmount * AdjustedLineQuantity), beforeAdjustment.PriceText);

        // (c) The counter arrives, on a temporary password, in a browser of its own. No virtual
        // authenticator: §17 accepts in as many words that the "counter role may operate password-only",
        // and this is the first scenario that exercises that shape end to end.
        IPage counter = await instance.OpenIsolatedPageAsync();

        await AccountJourneys.SignInWithPasswordAsync(
            counter, counterAccount.Username, counterAccount.TemporaryPassword);

        // §3.5's obligation (1), and worth its own assertion rather than being absorbed into the journey:
        // an account created by §3.7 carries must_change_password, and a counter who could reach the till
        // on a password an administrator can still read off a screen would be a real hole. The middleware
        // intercepts the sign-in's own navigation, so this is where it lands rather than somewhere it
        // asked for.
        Assert.Contains(
            AccountRoutes.ForcedPasswordChange, counter.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            counter, counterAccount.TemporaryPassword, CounterPassword);

        // (d) The till. Found the way a counter finds it — the board, the open-sittings query, the link —
        // and cross-checked against the row, because a screen showing a bill cannot tell "opened this
        // table's sitting" from "opened a sitting".
        Guid openedSitting = await CounterJourneys.OpenSittingAsync(
            counter, tableLabel, InteractivityPatience);

        OpenSitting? sitting = await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(sitting);
        Assert.Equal(sitting!.SittingIdentifier, openedSitting);

        // The bill as the till reads it, before the adjustment. This figure comes from the sitting_bill
        // view in SQL while the guest's own lines come from the event fold, so the two are genuinely
        // independent opinions about the same money — which is the whole reason both are asserted.
        CounterBill onArrival = await CounterJourneys.ReadBillAsync(counter);

        Assert.Equal(tableLabel, onArrival.TableLabel);
        Assert.Equal(Money(unadjustedTableTotal), onArrival.RunningTotalText);

        CounterBillEntry theirBill = Assert.Single(onArrival.People);

        Assert.Equal(guestAccount.DisplayName, theirBill.BillName);
        Assert.Equal(Money(unadjustedTableTotal), theirBill.PersonTotalText);

        CounterBillLine pieAtTheTill = Assert.Single(theirBill.Lines, line => line.Name == pie.Name);

        Assert.Equal(AdjustedLineQuantity, pieAtTheTill.Quantity);
        Assert.Equal(Money(pie.PriceAmount), pieAtTheTill.UnitPriceText);
        Assert.False(pieAtTheTill.IsDelivered);

        // (e) The adjustment. §11.3's dialog demands a reason and §6.5.7 requires one, so this is the one
        // mutation in the system that cannot be made silently — which is exactly what makes it worth
        // showing the guest.
        await CounterJourneys.AdjustPriceAsync(
            counter, pie.Name, AdjustedPieUnitPrice, PriceAdjustmentReason);

        // (f) "Guest sees old → new with reason." Nobody has touched the guest's phone: no reload, no
        // click, no navigation. The workflow published OrderLinesChanged after the transaction committed
        // (§9), the guest's circuit re-read, and §11.1 wrote a sentence under the line. This is the
        // assertion the scenario exists for.
        GuestOrderLineDetail adjusted = await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            pie.Name,
            line => line.PriceAdjustments.Count == 1,
            LiveUpdatePatience,
            "the counter's price adjustment written under the pie",
            cancellationToken);

        GuestPriceAdjustment shown = Assert.Single(adjusted.PriceAdjustments);

        // Both halves, from the elements that carry them rather than from the prose around them. "Old"
        // is the price the line was at when the adjustment was applied, which OrderNarrative captures
        // as it folds — not the menu price, though here they are the same because nothing had moved it
        // before.
        Assert.Equal(Money(pie.PriceAmount), shown.PreviousPriceText);
        Assert.Equal(Money(AdjustedPieUnitPrice), shown.NewPriceText);

        // "With reason", and with the right actor. §6.2 binds a price_adjustment to counter or
        // administrator and the surface renders whichever it was; a bill that named the wrong one is a
        // bill nobody can ask a question about. This is what the staff account in (a) was for.
        Assert.Contains(PriceAdjustmentReason, shown.Sentence, StringComparison.Ordinal);
        Assert.Contains("the counter", shown.Sentence, StringComparison.Ordinal);

        // The money moved with the sentence. A unit price is what was adjusted (§6.5.7), so a line of two
        // must move by twice the difference — the arithmetic being visible on the guest's own screen is
        // the difference between an audit trail and a note.
        Assert.Equal(Money(AdjustedPieUnitPrice * AdjustedLineQuantity), adjusted.PriceText);

        // And nothing else about the line changed. A price adjustment is not a fulfillment and not a
        // removal: §6.5.7 touches the price and the price only, and a badge that flipped here would mean
        // the guest had been told their food was on the table by somebody changing its price.
        Assert.Equal(GuestLineBadge.WithTheKitchen, adjusted.Badge);

        // (g) The soup is the control, and it is the half of "one line, not the ticket" worth getting
        // wrong. It carries no adjustment and still costs what the menu said.
        GuestOrderLineDetail untouched = await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            soup.Name,
            line => line.PriceAdjustments.Count == 0,
            LiveUpdatePatience,
            "the soup with no adjustment against it",
            cancellationToken);

        Assert.Equal(Money(soup.PriceAmount), untouched.PriceText);
        Assert.Equal(GuestLineBadge.WithTheKitchen, untouched.Badge);

        // (h) And the till agrees, through SQL rather than through the fold. sitting_bill sums
        // order_current_line's extended prices, so this is the same adjustment observed by the other of
        // the two paths §8.3 keeps — a number that moved on one and not the other would be the view/fold
        // divergence §16.2 exists to prevent, seen from a screen.
        CounterBill afterAdjustment = await CounterJourneys.ReadBillAsync(counter);

        Assert.Equal(Money(adjustedTableTotal), afterAdjustment.RunningTotalText);

        CounterBillEntry rebilled = Assert.Single(afterAdjustment.People);

        Assert.Equal(Money(adjustedTableTotal), rebilled.PersonTotalText);

        CounterBillLine pieRebilled = Assert.Single(rebilled.Lines, line => line.Name == pie.Name);

        Assert.Equal(Money(AdjustedPieUnitPrice), pieRebilled.UnitPriceText);
        Assert.Equal(Money(AdjustedPieUnitPrice * AdjustedLineQuantity), pieRebilled.LineTotalText);

        CounterBillLine soupRebilled = Assert.Single(rebilled.Lines, line => line.Name == soup.Name);

        Assert.Equal(Money(soup.PriceAmount), soupRebilled.UnitPriceText);
    }

    // -------------------------------------------------------------------------------------------
    // 10. Counter closes (pending-line warning shown) → table flips to settled read-only; totals
    //     match.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Counter_ClosesSitting_TableFlipsToSettledAndTotalsMatch()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Ten";
        GuestAccount guestAccount = new("e2e.guest.ten", "Ten Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        // (a) An administrator, two things on the menu, a table — and a counter account, for a reason
        // that is the mirror image of scenario 9's. There the actor was recorded and rendered, so the
        // role had to be right. Here nothing records who pressed Close except the row, and §11.3 gates
        // every control on `_sitting.IsOpen` without consulting the principal at all — so an
        // administrator would produce the identical screen and the assertion would pass either way.
        //
        // It is the direction of the *next* failure that decides it. §6.5.8 admits an administrator's
        // corrective events after a close and §5.3 says corrections "are an administrator's", so the day
        // this surface grows the correction panel those sections describe, an administrator at a settled
        // till will correctly see controls a counter must not. Asserting "read-only" as an administrator
        // is asserting it for the one role permitted to act after a close; the counter is the role for
        // which read-only is unconditional, and that is the claim §11.3 makes.
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        // Before the guest is seated, on scenario 9's reasoning: §8.4's clock starts at the send, and
        // there is no sense in spending any of it on an Argon2id hash and four form posts. Nothing here
        // asserts on a reminder and at the default sixty seconds one would be harmless if it arrived —
        // it writes a kitchen_notification row and raises a badge, neither of which is on this
        // scenario's screen — but it is still the right order.
        StaffAccount counterAccount = await AdministrationJourneys.CreateStaffAccountAsync(
            administrator, "e2e.counter.ten", "Ten Counter", StaffRoles.Counter);

        // The one number this scenario is about, derived from the prices it actually created. One soup
        // delivered and two pies that never leave the kitchen — §5.3's "knowingly charge" is the whole
        // point, so the money that gets stamped has to include food nobody ate.
        decimal tableTotal = soup.PriceAmount + (UndeliveredLineQuantity * pie.PriceAmount);
        string expectedTotal = Money(tableTotal);

        // (b) The guest, seated, with both lines on the pass.
        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(guest, soup, 1);
        await TableOrderJourneys.StageAsync(guest, pie, UndeliveredLineQuantity);
        await TableOrderJourneys.SendAsync(guest);

        IReadOnlyList<GuestOrderLine> sent = await TableOrderJourneys.WaitForCommittedLinesAsync(
            guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(sent, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        // (c) The kitchen delivers the soup and only the soup. This is what makes the rest of the
        // scenario a mixed bill rather than a uniform one: §5.3's warning has exactly one line to name,
        // and the settled total has to be a sum over a delivered line and an undelivered one.
        //
        // The administrator stands at the pass here, on the reasoning scenarios 4, 6, 7 and 8 give: §3.7
        // admits both, KitchenBoard.razor records whoever acted as themselves, and nothing below asserts
        // on who cooked. The counter account exists for the till, and standing up a second staff account
        // for one tap would be a sign-in and a forced password change this scenario never looks at.
        await KitchenJourneys.OpenAsync(administrator, InteractivityPatience);

        await KitchenJourneys.WaitForBoardAsync(
            administrator,
            board => board.PendingLines.Count == 2,
            LiveUpdatePatience,
            "both of the guest's lines waiting on the pass",
            cancellationToken);

        await KitchenJourneys.FulfillLineAsync(administrator, soup.Name);

        // Read on the guest's own screen rather than on the board, because the board's proof that the
        // soup left is that it stopped rendering it — and this scenario needs the positive form: the
        // guest has been told their soup is at the table, and their pie has not.
        await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            soup.Name,
            line => line.Badge == GuestLineBadge.AtYourTable,
            LiveUpdatePatience,
            "the soup re-badged as delivered",
            cancellationToken);

        // (d) The counter arrives, on a temporary password, in a browser of its own. No virtual
        // authenticator: §17 accepts that the "counter role may operate password-only".
        IPage counter = await instance.OpenIsolatedPageAsync();

        await AccountJourneys.SignInWithPasswordAsync(
            counter, counterAccount.Username, counterAccount.TemporaryPassword);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, counter.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            counter, counterAccount.TemporaryPassword, ClosingCounterPassword);

        // (e) The till, found the way a counter finds it — the board, the open-sittings query, the link —
        // and cross-checked against the row, because a screen showing a bill cannot tell "opened this
        // table's sitting" from "opened a sitting".
        Guid sittingIdentifier = await CounterJourneys.OpenSittingAsync(
            counter, tableLabel, InteractivityPatience);

        OpenSitting? openSitting =
            await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(openSitting);
        Assert.Equal(openSitting!.SittingIdentifier, sittingIdentifier);

        // Reading (1): the till's header, which is a SQL sum over the sitting_bill view.
        CounterBill beforeClose = await CounterJourneys.ReadBillAsync(counter);

        Assert.Equal(tableLabel, beforeClose.TableLabel);
        Assert.Equal(expectedTotal, beforeClose.RunningTotalText);

        CounterBillEntry theirBill = Assert.Single(beforeClose.People);

        Assert.Equal(guestAccount.DisplayName, theirBill.BillName);
        Assert.Equal(expectedTotal, theirBill.PersonTotalText);

        CounterBillLine soupAtTheTill = Assert.Single(theirBill.Lines, line => line.Name == soup.Name);
        CounterBillLine pieAtTheTill = Assert.Single(theirBill.Lines, line => line.Name == pie.Name);

        Assert.True(soupAtTheTill.IsDelivered);
        Assert.False(pieAtTheTill.IsDelivered);
        Assert.Equal(UndeliveredLineQuantity, pieAtTheTill.Quantity);

        // (f) §5.3: "the counter UI must surface still-pending lines prominently before offering Close".
        // Asserted before anything is pressed, because "before" is half of what that sentence requires —
        // a warning that appeared alongside the confirmation would satisfy a scenario that only checked
        // it was there somewhere, and would be useless to the person deciding.
        CounterPendingWarning? warning = await CounterJourneys.ReadPendingWarningAsync(counter);

        Assert.NotNull(warning);

        // One line, not two items and not two pies. §11.3 renders PendingLineCount, which counts rows in
        // order_current_line that are not fulfilled — so the quantity on the line is deliberately not the
        // number here, and a warning that had started counting portions would say "2".
        Assert.Equal(1, warning!.LineCount);
        Assert.Contains("still with the kitchen", warning.Sentence, StringComparison.Ordinal);

        // (g) The confirmation, and reading (2): the amount §11.3 quotes back, which comes from
        // CurrentTotalAmount directly rather than through either sum above. This is the last number a
        // person reads before a write §5.3 says cannot be undone, and it is the worst possible place for
        // the three readings to disagree.
        CloseConfirmation confirmation = await CounterJourneys.BeginCloseAsync(counter);

        Assert.Equal(expectedTotal, confirmation.AmountText);
        Assert.Contains(tableLabel, confirmation.Sentence, StringComparison.Ordinal);

        // (h) The close. Readings (3) and (4): the header now shows the *stamped* total under a label
        // that says so, and the settle panel shows a C# sum over the per-person entries.
        SettledTill settled = await CounterJourneys.ConfirmCloseAsync(counter, InteractivityPatience);

        Assert.True(
            settled.SaysReadOnly,
            "§11.3 must say a settled sitting is settled: " + CounterJourneys.DescribeSettled(settled));

        Assert.Equal("Settled total", settled.TotalLabel);
        Assert.Equal(expectedTotal, settled.TotalText);
        Assert.Equal(expectedTotal, settled.TableTotalText);

        // Nothing has corrected anything, so §5.3's second figure must be absent. A settled total that
        // had already acquired a "corrected to" number seconds after the close would mean the stamped
        // value and the live one diverged on their own, which is the one thing §5.3 promises cannot
        // happen.
        Assert.False(
            settled.ShowsCorrection,
            "no §6.7 correction has been made, so no corrected total should be shown: "
                + CounterJourneys.DescribeSettled(settled));

        // "Read-only" as an absence of doors rather than as a sentence. §6.5.8 admits nothing but an
        // administrator's corrective events after a close, so a counter's Adjust, Remove, Add and Close
        // would all be doors that only ever answer no.
        Assert.Equal(0, settled.LineControlCount);
        Assert.False(settled.OffersStaffAdd);
        Assert.False(settled.OffersClose);

        // Who settled it, on the header where a guest querying a receipt would be pointed. This is the
        // one place the counter account is visible on screen, and it is why the scenario made one.
        Assert.Contains(counterAccount.DisplayName, settled.HeaderMeta, StringComparison.Ordinal);

        // §5.3 again, from the other end: the confirmation records what was charged anyway. A close that
        // quietly dropped the undelivered line — or quietly delivered it — would produce a different
        // sentence here and a different total above.
        Assert.NotNull(settled.Notice);
        Assert.Contains(expectedTotal, settled.Notice!, StringComparison.Ordinal);
        Assert.Contains("still with the kitchen", settled.Notice, StringComparison.Ordinal);

        // The pie is still undelivered on a settled bill, and that is correct. §5.3's "knowingly charge"
        // is a record, not a rounding: a surface that re-badged the line at close would be telling the
        // guest their food arrived, which is the one thing on this bill they might want to argue about.
        CounterBill afterClose = await CounterJourneys.ReadBillAsync(counter);
        CounterBillEntry rebilled = Assert.Single(afterClose.People);
        CounterBillLine pieAfterClose = Assert.Single(rebilled.Lines, line => line.Name == pie.Name);

        Assert.False(pieAfterClose.IsDelivered);

        // (i) "The table flips to settled read-only" — on the guest's phone, which nobody has touched.
        // §9 published SittingClosed after the transaction committed, the guest's circuit re-read,
        // GetOpenSittingForMemberAsync now answers null because closed_at is set, and §11.1's ordering
        // apparatus stopped being rendered. Readings (5) and (6) are here: both totals, computed in C#
        // on a different circuit from every reading above.
        GuestSettledView guestView = await TableOrderJourneys.WaitForSettledViewAsync(
            guest, LiveUpdatePatience, cancellationToken);

        Assert.False(
            guestView.OffersPicker,
            "a settled sitting must offer the guest nothing to order: "
                + TableOrderJourneys.DescribeSettledView(guestView));

        Assert.False(guestView.OffersSend);
        Assert.Equal(0, guestView.RemovalCheckboxes);

        Assert.Equal(expectedTotal, guestView.Totals.TableTotalText);

        // One guest at the table, so their own total is the table's. Asserted rather than assumed: these
        // are two different sums in the component — one filtered to this person, one over every entry —
        // and a filter that had stopped filtering would show the same number for the wrong reason only
        // when the party is one, which is exactly this scenario.
        Assert.Equal(expectedTotal, guestView.Totals.YourTotalText);

        // The record survives the close, badges and all.
        Assert.Equal(2, guestView.Lines.Count);

        GuestOrderLineDetail soupOnTheBill =
            Assert.Single(guestView.Lines, line => line.Name.Contains(soup.Name, StringComparison.Ordinal));
        GuestOrderLineDetail pieOnTheBill =
            Assert.Single(guestView.Lines, line => line.Name.Contains(pie.Name, StringComparison.Ordinal));

        Assert.Equal(GuestLineBadge.AtYourTable, soupOnTheBill.Badge);
        Assert.Equal(GuestLineBadge.WithTheKitchen, pieOnTheBill.Badge);
        Assert.Equal(Money(pie.PriceAmount * UndeliveredLineQuantity), pieOnTheBill.PriceText);

        // (j) Reading (7), and the only one that is not another rendering of the same query: the column
        // §5.3 stamped. Every figure above is computed at render time from sitting_bill, so all six could
        // agree perfectly on a close that wrote no total at all — and "never rewritten" is a promise
        // about this value rather than about any of them.
        SettledSitting? row =
            await instance.ReadSettledSittingAsync(sittingIdentifier, cancellationToken);

        Assert.NotNull(row);
        Assert.Equal(tableTotal, row!.SettledTotalAmount);
        Assert.Equal(counterAccount.Username, row.ClosedByUsername);

        // And the table has no open sitting any more. The partial unique index permits exactly one, so
        // this is the row-level form of "the table left the floor" — and it is what makes the next guest
        // to scan this table open a new sitting rather than rejoin a settled one.
        Assert.Null(await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken));

        // (k) The board, which is where "flips to settled" is most literally true: the table leaves the
        // floor and appears under §11.3's read-only "Settled today" list at the stamped amount. Both
        // halves together, because a table on neither list has vanished and a table on both is two rows
        // for one sitting.
        await counter.GotoAsync(CounterJourneys.BoardPath);
        await CounterJourneys.WaitForBoardAsync(counter, InteractivityPatience);

        CounterFloor floor = await CounterJourneys.ReadFloorAsync(counter);

        Assert.DoesNotContain(tableLabel, floor.OpenTableLabels);

        SettledTableRow settledRow = Assert.Single(
            floor.Settled, candidate => candidate.TableLabel == tableLabel);

        Assert.Equal(expectedTotal, settledRow.AmountText);
        Assert.Contains(counterAccount.DisplayName, settledRow.SettledBy, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Still to come. Each is one required §16.3 scenario, named so the matrix stays legible.
    // -------------------------------------------------------------------------------------------

    // -------------------------------------------------------------------------------------------
    // 11. Guest hides a closed order → gone from own history (staff/admin unchanged); admin
    //     filters the hidden-records view by username → Unhide restores it.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Guest_HidesClosedOrder_AdminCanUnhide()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Eleven";

        // Two guests, and neither username is a substring of the other. §6.8's filter is a literal
        // substring match (DapperOrderHistoryReads.SubstringPattern), so a pair like ".one" and ".one.b"
        // would make "filtering by the bystander finds nothing" pass or fail for reasons of spelling.
        GuestAccount hider = new("e2e.guest.eleven.alpha", "Eleven Alpha");
        GuestAccount bystander = new("e2e.guest.eleven.bravo", "Eleven Bravo");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        // (a) An administrator, two things on the menu, a table.
        //
        // No counter account this time, and the contrast with scenarios 9 and 10 is the reason to say so.
        // There the role was load-bearing — §6.2 records who adjusted a price and §11.1 renders it; §11.3
        // makes read-only unconditional for a counter and conditional for an administrator. Here the
        // close is arrangement rather than subject: §6.8 refuses a hide on an open sitting, so this
        // scenario needs a settled one and does not care who settled it. §3.7 admits administrators to
        // /counter, and standing up a staff account would add a sign-in and a forced password change to a
        // scenario that asserts on neither.
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        // Three figures, all derived from the prices this scenario created, and all different from one
        // another. The whole point of the third is that a page showing the table's total where a person's
        // belongs cannot pass by coincidence.
        decimal hiderTotal = soup.PriceAmount + (HiddenOrderPieQuantity * pie.PriceAmount);
        decimal bystanderTotal = soup.PriceAmount;

        string expectedHiderTotal = Money(hiderTotal);
        string expectedBystanderTotal = Money(bystanderTotal);
        string expectedTableTotal = Money(hiderTotal + bystanderTotal);

        // (b) Two guests at one table with two different bills.
        //
        // <b>The second guest is what makes the central assertion mean anything.</b> §6.8's promise is
        // that a hide removes one order from one person's own view and touches nothing else. With a
        // single guest, "their history is empty afterwards" is satisfied equally well by a page that
        // stopped rendering, by a reader that started returning nothing, and by a hide that hid the
        // sitting — and all three are catastrophic. A bystander whose own history is unchanged across the
        // same write separates "this order was hidden" from "history broke", and costs one registration
        // rather than the second sitting a per-order claim would otherwise need.
        IPage alpha = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, hider, cancellationToken);

        await TableOrderJourneys.StageAsync(alpha, soup, 1);
        await TableOrderJourneys.StageAsync(alpha, pie, HiddenOrderPieQuantity);
        await TableOrderJourneys.SendAsync(alpha);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            alpha,
            lines => lines.Count == HiddenOrderLineCount,
            LiveUpdatePatience,
            "both of the hider's sent lines",
            cancellationToken);

        IPage bravo = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, bystander, cancellationToken);

        await TableOrderJourneys.StageAsync(bravo, soup, 1);
        await TableOrderJourneys.SendAsync(bravo);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            bravo,
            lines => lines.Count == 1,
            LiveUpdatePatience,
            "the bystander's one sent line",
            cancellationToken);

        // (c) The close, because §6.8 refuses a hide while the sitting is open — "that table is still
        // open, so its order is not history yet". Nothing is fulfilled first: §5.3's pending-line warning
        // is scenario 10's subject and a close over undelivered food is exactly as valid here.
        Guid sittingIdentifier =
            await CounterJourneys.OpenSittingAsync(administrator, tableLabel, InteractivityPatience);

        CounterBill beforeClose = await CounterJourneys.ReadBillAsync(administrator);

        Assert.Equal(2, beforeClose.People.Count);
        Assert.Equal(expectedTableTotal, beforeClose.RunningTotalText);

        await CounterJourneys.BeginCloseAsync(administrator);

        SettledTill settled = await CounterJourneys.ConfirmCloseAsync(administrator, InteractivityPatience);

        Assert.Equal("Settled total", settled.TotalLabel);
        Assert.Equal(expectedTableTotal, settled.TotalText);

        // (d) Both histories, before anything is hidden. §11.1: "the guest's own past orders at this
        // restaurant — cross-member history is never shown", so each of these lists holds exactly one
        // order and it is that person's.
        await HistoryJourneys.OpenAsync(alpha, InteractivityPatience);

        GuestHistory alphaBefore = await HistoryJourneys.ReadAsync(alpha);
        HistoryOrder theirs = Assert.Single(alphaBefore.Orders);

        Assert.Equal(tableLabel, theirs.TableLabel);
        Assert.Equal(expectedHiderTotal, theirs.PersonTotalText);
        Assert.Equal(HiddenOrderLineCount, theirs.LineCount);

        // The pies at three, on the line the history renders. §11.1 shows the projection rather than the
        // log, so this is the extension — and a history that had started showing unit prices would read
        // 14.00 against a quantity of three.
        HistoryLine pieInHistory =
            Assert.Single(theirs.Lines, line => line.Name == pie.Name);

        Assert.Equal(HiddenOrderPieQuantity, pieInHistory.Quantity);
        Assert.Equal(Money(pie.PriceAmount * HiddenOrderPieQuantity), pieInHistory.LineTotalText);

        await HistoryJourneys.OpenAsync(bravo, InteractivityPatience);

        GuestHistory bravoBefore = await HistoryJourneys.ReadAsync(bravo);
        HistoryOrder bystanderOrder = Assert.Single(bravoBefore.Orders);

        Assert.Equal(expectedBystanderTotal, bystanderOrder.PersonTotalText);
        Assert.NotEqual(theirs.GuestOrderIdentifier, bystanderOrder.GuestOrderIdentifier);

        // (e) The hide, confirmed the way §6.8 requires: not a browser dialog but a step that "states
        // plainly that this cannot be undone from the guest's account".
        await HistoryJourneys.HideAsync(alpha, theirs.GuestOrderIdentifier, InteractivityPatience);

        GuestHistory alphaAfter = await HistoryJourneys.ReadAsync(alpha);

        Assert.Empty(alphaAfter.Orders);

        // Gone <em>and</em> the page saying it has nothing, which are two claims. A list that failed to
        // render is also empty, and §11.1 draws this sentence only when the reader came back with
        // nothing — so its absence beside an empty list would mean the page broke rather than that the
        // order was hidden.
        Assert.NotNull(alphaAfter.EmptySentence);
        Assert.Contains("Nothing here yet", alphaAfter.EmptySentence!, StringComparison.Ordinal);

        Assert.NotNull(alphaAfter.Notice);
        Assert.Contains("a manager can restore it", alphaAfter.Notice!, StringComparison.Ordinal);

        // (f) Nobody else's history moved. Re-read from the server rather than from the DOM the
        // bystander's browser was already holding: nothing broadcasts a hide to another guest's circuit,
        // and this page is static SSR anyway, so a stale document would agree with this assertion without
        // having been asked.
        await HistoryJourneys.OpenAsync(bravo, InteractivityPatience);

        GuestHistory bravoAfter = await HistoryJourneys.ReadAsync(bravo);
        HistoryOrder untouched = Assert.Single(bravoAfter.Orders);

        Assert.Equal(bystanderOrder.GuestOrderIdentifier, untouched.GuestOrderIdentifier);
        Assert.Equal(expectedBystanderTotal, untouched.PersonTotalText);

        // (g) §16.3's "staff/admin unchanged", which is §6.8's own sentence from the other end: the order
        // is "still on its sitting's bill, still in the kitchen's and the counter's records, and still in
        // the settled total". Re-opened by identifier so the bill is a fresh server read — §11.3's
        // closed-sitting lookup — because the administrator's browser has been on that page since the
        // close and a stale DOM would pass this without being asked either.
        await CounterJourneys.OpenSettledSittingAsync(
            administrator, sittingIdentifier, InteractivityPatience);

        CounterBill afterHide = await CounterJourneys.ReadBillAsync(administrator);

        Assert.Equal(2, afterHide.People.Count);
        Assert.Equal(expectedTableTotal, afterHide.RunningTotalText);

        CounterBillEntry hidersBill =
            Assert.Single(afterHide.People, entry => entry.BillName == hider.DisplayName);

        Assert.Equal(expectedHiderTotal, hidersBill.PersonTotalText);
        Assert.Equal(HiddenOrderLineCount, hidersBill.Lines.Count);

        // The stamped column too, past every surface. §6.8 changes a visibility flag and §5.3 promises the
        // settled total is never rewritten; a hide that had reached the money would be a defect no screen
        // above could distinguish from correct behaviour, because all of them would agree.
        SettledSitting? row =
            await instance.ReadSettledSittingAsync(sittingIdentifier, cancellationToken);

        Assert.NotNull(row);
        Assert.Equal(hiderTotal + bystanderTotal, row!.SettledTotalAmount);

        // (h) §11.4's hidden-records view, which §6.8 calls the only way anyone can find a hidden order
        // again. Unfiltered first, because §11.4 says administration starts complete and narrows "only on
        // explicit request".
        await HiddenRecordJourneys.OpenAsync(administrator, InteractivityPatience);

        HiddenRecordList everything = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.False(
            everything.IsNarrowed,
            "§11.4's view must open unfiltered: " + HiddenRecordJourneys.Describe(everything));

        HiddenRecordRow found = Assert.Single(everything.Rows);

        // The identifier, and this is the assertion the whole apparatus exists for. "A row appeared" is
        // satisfied by any hidden order in the restaurant; that the row administration found is the order
        // this guest hid is a claim about two identifiers, both read off links the surfaces rendered.
        Assert.Equal(theirs.GuestOrderIdentifier, found.GuestOrderIdentifier);
        Assert.Equal(sittingIdentifier, found.SittingIdentifier);
        Assert.Equal(hider.Username, found.Username);
        Assert.Equal(hider.DisplayName, found.OwnerName);
        Assert.Equal(expectedHiderTotal, found.PersonTotalText);

        // (i) §6.8's username filter, in both directions. One of the two is the assertion §16.3 names;
        // the other is what stops it being vacuous — a filter that had quietly stopped filtering would
        // return this row for every username there is, and would satisfy the positive case perfectly.
        await HiddenRecordJourneys.FilterByUsernameAsync(
            administrator, bystander.Username, InteractivityPatience);

        HiddenRecordList wrongOwner = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.Empty(wrongOwner.Rows);
        Assert.True(
            wrongOwner.IsNarrowed,
            "the list must know it is filtered: " + HiddenRecordJourneys.Describe(wrongOwner));

        Assert.NotNull(wrongOwner.EmptySentence);
        Assert.Contains("matches that", wrongOwner.EmptySentence!, StringComparison.Ordinal);

        await HiddenRecordJourneys.FilterByUsernameAsync(
            administrator, hider.Username, InteractivityPatience);

        HiddenRecordList rightOwner = await HiddenRecordJourneys.ReadAsync(administrator);
        HiddenRecordRow filtered = Assert.Single(rightOwner.Rows);

        Assert.Equal(theirs.GuestOrderIdentifier, filtered.GuestOrderIdentifier);

        // (j) The complete record §6.8 requires the row to expand to: "full event log, visibility log,
        // sitting context, unprojected".
        HiddenRecordDetail detail = await HiddenRecordJourneys.ExpandAsync(
            administrator, theirs.GuestOrderIdentifier, InteractivityPatience);

        HiddenVisibilityEntry onlyEvent = Assert.Single(detail.VisibilityLog);

        Assert.Equal("Hidden by the owner", onlyEvent.Description);

        // Who hid it, which is the one place §6.8's actor is nameable. The stored word is "hidden" for
        // both the guest's act and, by symmetry with "unhidden", carries no actor of its own — so a
        // surface that had recorded the wrong person here would read identically.
        Assert.Contains(hider.DisplayName, onlyEvent.ActorAndTime, StringComparison.Ordinal);

        // The order's own history survived being hidden. §6.8 hides a record from one view; ADR-0002 says
        // the log outlives the state, and a hide that had taken the events with it would leave an
        // administrator holding the one screen that is supposed to be able to answer for it and nothing
        // to answer with.
        Assert.True(
            detail.EventCount >= 1,
            "§11.4 must show the order's stored events under a hidden record; the visibility log holds "
                + HiddenRecordJourneys.DescribeVisibilityLog(detail.VisibilityLog));

        Assert.True(detail.OffersUnhide);

        // (k) The unhide. The row leaves the list, which is what makes the button's effect visible.
        await HiddenRecordJourneys.UnhideAsync(administrator, InteractivityPatience);

        HiddenRecordList afterUnhide = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.Empty(afterUnhide.Rows);
        Assert.NotNull(afterUnhide.Notice);
        Assert.Contains("back on its owner's history", afterUnhide.Notice!, StringComparison.Ordinal);

        // Still filtered, because §11.4 redirects back to the same question it was asked — so the sentence
        // above is the narrowed one. The stronger claim needs the filter cleared, and it is worth making
        // separately: "nothing matches this username" and "nothing is hidden anywhere" are different
        // facts, and only the second one says the restaurant is back where it started.
        Assert.True(afterUnhide.IsNarrowed);

        await HiddenRecordJourneys.OpenAsync(administrator, InteractivityPatience);

        HiddenRecordList clean = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.Empty(clean.Rows);
        Assert.False(clean.IsNarrowed);
        Assert.NotNull(clean.EmptySentence);
        Assert.Contains(
            "anywhere in the restaurant", clean.EmptySentence!, StringComparison.Ordinal);

        // (l) And back on the owner's history, whole. §6.8's "unhidden" row is now the latest visibility
        // event, so order_visibility_current answers false and the reader stops excluding it — the same
        // query that omitted it four steps ago, on the same person, with a different answer.
        await HistoryJourneys.OpenAsync(alpha, InteractivityPatience);

        GuestHistory restored = await HistoryJourneys.ReadAsync(alpha);
        HistoryOrder back = Assert.Single(restored.Orders);

        Assert.Equal(theirs.GuestOrderIdentifier, back.GuestOrderIdentifier);
        Assert.Equal(tableLabel, back.TableLabel);
        Assert.Equal(expectedHiderTotal, back.PersonTotalText);
        Assert.Equal(HiddenOrderLineCount, back.LineCount);

        // Restored rather than merely listed: the lines are the ones that were there before, at the
        // quantities they were at. A visibility flag is not supposed to be able to touch any of this, and
        // this is the assertion that says so.
        HistoryLine pieRestored = Assert.Single(back.Lines, line => line.Name == pie.Name);

        Assert.Equal(HiddenOrderPieQuantity, pieRestored.Quantity);
        Assert.Equal(Money(pie.PriceAmount * HiddenOrderPieQuantity), pieRestored.LineTotalText);

        // Nobody else's history moved across the unhide either, for the same reason it mattered across the
        // hide — one is a claim about a write and this is a claim about its inverse.
        await HistoryJourneys.OpenAsync(bravo, InteractivityPatience);

        GuestHistory bravoFinal = await HistoryJourneys.ReadAsync(bravo);
        HistoryOrder stillOne = Assert.Single(bravoFinal.Orders);

        Assert.Equal(bystanderOrder.GuestOrderIdentifier, stillOne.GuestOrderIdentifier);
    }

    // -------------------------------------------------------------------------------------------
    // 12. Admin resets a TOTP-enrolled user → password sign-in → forced password change → forced
    //     TOTP re-enrollment → lands home; the passkey sign-in path also hits the pipeline.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Admin_ResetsTotpUser_ForcesPasswordThenTotpReenrollment()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        // (a) An administrator, and a staff account for them to reset.
        //
        // The kitchen role, chosen for two reasons rather than arbitrarily. §3.4's authenticator is a
        // staff credential — §17 accepts a password-only counter but nothing in the specification asks a
        // guest to carry TOTP — so a staff account is the faithful subject of "a TOTP-enrolled user". And
        // the role gives the closing claim something to point at: MainLayout renders the kitchen link to
        // the kitchen role and to nobody else (not even to administrators), so "landed home" can be
        // "landed home as this person, with this role's door on screen" rather than "reached a page".
        // Scenarios 9 and 10 use the counter role; a fixture of its own keeps a failure here unambiguous.
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        StaffAccount kitchenAccount = await AdministrationJourneys.CreateStaffAccountAsync(
            administrator, "e2e.kitchen.twelve", "Twelve Kitchen", StaffRoles.Kitchen);

        // (b) The account has to enrol itself, and it takes four form posts to get there.
        //
        // §3.7's create-staff form writes must_change_password and nothing else: no secret, no passkey,
        // and deliberately not must_enroll_totp. So the state this scenario's first sentence starts from
        // — an enrolled account, with a passkey — cannot be arranged by an administrator and would not be
        // worth arranging by INSERT: the reset under test probes totp_secret_protected to decide whether
        // to clear an authenticator at all, so a fixture that got that column wrong would produce a
        // password-only reset and the scenario's second obligation would never exist.
        //
        // Two browsers for one person, and the split is not tidiness. A WebAuthn private key never leaves
        // the authenticator that minted it, so the passkey registered below belongs to this context for
        // good. It cannot be the same context that later signs in by password: passkey.js fires a
        // conditional-mediation credentials.get() on every sign-in page load, and this authenticator
        // reports a resident key and simulates presence automatically — so once a discoverable credential
        // exists here, a "password sign-in" on this page may be answered by the authenticator before a
        // password is ever typed, and would still land on the forced-change page. The scenario would pass
        // for the wrong reason. The terminal in (f) has no authenticator, so its conditional request is
        // never satisfied and the password path is genuinely the password path.
        IPage device = await instance.OpenIsolatedPageAsync(withVirtualAuthenticator: true);

        await AccountJourneys.SignInWithPasswordAsync(
            device, kitchenAccount.Username, kitchenAccount.TemporaryPassword);

        // This first sign-in is safe on the passkey-bearing context precisely because the authenticator
        // is still empty: there is no credential for this relying party, so the conditional request has
        // nothing to answer with. Everything after (d) is on the other browser.
        Assert.Contains(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            device, kitchenAccount.TemporaryPassword, FirstKitchenPassword);

        IReadOnlyList<string> codesBeforeReset = await AccountJourneys.EnrollAuthenticatorAsync(device);

        Assert.Equal(AccountJourneys.ExpectedRecoveryCodeCount, codesBeforeReset.Count);
        Assert.All(codesBeforeReset, code => Assert.False(string.IsNullOrWhiteSpace(code)));

        Assert.Equal(1, await AccountJourneys.AddPasskeyAsync(device));

        await AccountJourneys.SignOutAsync(device);

        // (c) The account as administration sees it, before anything is reset.
        //
        // Asserted first, and it is not scene-setting: every claim in (d) is of the form "this chip is
        // there now", and a chip that had always been there would satisfy it. The pair is the assertion.
        ManagedAccount before = await AdministrationJourneys.ReadAccountFactsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.Equal(kitchenAccount.Username, before.Username);
        Assert.Equal("Active", Assert.Single(before.StatusChips));
        Assert.Equal("kitchen", Assert.Single(before.Roles));
        Assert.Contains("Password", before.Credentials);
        Assert.Contains("Authenticator", before.Credentials);

        // (d) The reset. Both halves of §3.7's conditional fire, because (b) left an authenticator to
        // clear — and the panel saying so is what makes the rest of this scenario reachable.
        CredentialReset reset = await AdministrationJourneys.ResetCredentialsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.True(reset.ClearedAuthenticator);
        Assert.NotEqual(string.Empty, reset.TemporaryPassword);

        // A fresh one, not the creation password shown back. The two are minted by different code paths
        // for different reasons and confusing them would be invisible from any other assertion here.
        Assert.NotEqual(kitchenAccount.TemporaryPassword, reset.TemporaryPassword);

        ManagedAccount afterReset = await AdministrationJourneys.ReadAccountFactsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.Contains("Must change password", afterReset.StatusChips);
        Assert.Contains("Must set up authenticator", afterReset.StatusChips);

        // The account is still signable-into and still holds its role: §3.7's reset is a credential
        // change, and a reset that had quietly deactivated an account or dropped a grant would clear
        // every obligation assertion below and be caught by nothing else.
        Assert.Contains("Active", afterReset.StatusChips);
        Assert.Equal("kitchen", Assert.Single(afterReset.Roles));

        // Derived rather than stored (§3.4 has no enrolled column), so the missing chip is the surface
        // agreeing that totp_secret_protected really is NULL — and the password chip still being there is
        // what says the temporary one above was written rather than the row being emptied.
        Assert.Contains("Password", afterReset.Credentials);
        Assert.DoesNotContain("Authenticator", afterReset.Credentials);

        // (e) The passkey path hits the pipeline too — §16.3 scenario 12's last clause, and the one that
        // could most plausibly have been missed.
        //
        // A passkey sign-in is the credential that skips §3.5's second factor by construction (scenario
        // 13), and ObligationsMiddleware is credential-agnostic on purpose: it reads the obligation claims
        // off whatever cookie the sign-in issued. Nothing about the passkey was touched by the reset, so
        // this signs in successfully and is then held anyway.
        await AccountJourneys.SignInWithPasskeyAsync(device, kitchenAccount.Username);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, device.Url, StringComparison.Ordinal);

        // And "hits the pipeline" means §3.5's actual promise: no authenticated endpoint is reachable.
        // Asked for the one board this role could otherwise walk straight into, which is what makes the
        // refusal mean something — and the redirect carries the destination rather than defaulting, which
        // is the only place in this scenario where step (3)'s ReturnUrl is a non-trivial value and can
        // therefore be told apart from SafeLocalReturnUrl's fallback.
        await device.GotoAsync("/kitchen");

        Assert.Contains(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);
        Assert.Contains("ReturnUrl=%2Fkitchen", device.Url, StringComparison.Ordinal);

        // Signed out before the obligations are cleared elsewhere, and the order matters for (g). The
        // middleware decides from this cookie's claims, not from the row, so a session left open here
        // would still be redirected after (f) cleared both flags — and (g) could then not tell a released
        // principal from a page noticing its own claim had gone stale.
        await AccountJourneys.SignOutAsync(device);

        // (f) The password walk, all the way home, on a terminal that holds no passkey.
        IPage terminal = await instance.OpenIsolatedPageAsync();

        await AccountJourneys.SignInWithPasswordAsync(
            terminal, kitchenAccount.Username, reset.TemporaryPassword);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, terminal.Url, StringComparison.Ordinal);

        // No challenge, and this is a real claim rather than a restatement of scenario 13's. This account
        // was enrolled ten seconds ago; §3.7's reset nulled the secret, and two-factor in §3.4 is derived
        // from that column — so a password sign-in landing here rather than at /sign-in/two-factor is the
        // observable consequence of the clearing having actually happened.
        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, terminal.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            terminal, reset.TemporaryPassword, ReenrolledKitchenPassword);

        // Obligation (1) cleared, obligation (2) immediately taken up. ObligationsPipeline's order is
        // fixed and this is the whole of it observed from outside: the forced-change page re-issued the
        // cookie without must_change_password, and the very next request was intercepted for the flag
        // that is still set.
        Assert.Contains(AccountRoutes.ForcedTotpEnrollment, terminal.Url, StringComparison.Ordinal);

        IReadOnlyList<string> codesAfterReset = await AccountJourneys.CompleteForcedTotpEnrollmentAsync(
            terminal, AccountJourneys.LandingPageMarker, "the landing page");

        Assert.Equal(AccountJourneys.ExpectedRecoveryCodeCount, codesAfterReset.Count);

        // A genuinely fresh set. §3.7's reset deletes every totp_recovery_code row and §3.4 replaces the
        // set on confirmation, so an overlap of even one code would mean a code the administrator's reset
        // was supposed to have destroyed is still live — which no other assertion in this scenario or any
        // other would notice.
        Assert.Empty(codesAfterReset.Intersect(codesBeforeReset, StringComparer.Ordinal));

        // Lands home. "/" is also SafeLocalReturnUrl's fallback, so this landing on its own does not
        // separate "carried the destination across two redirects and two cookie re-issues" from "dropped
        // it"; (e)'s ReturnUrl=%2Fkitchen is where that is separable, and this is §16.3's own sentence.
        Assert.Equal("/", new Uri(terminal.Url).AbsolutePath);

        // A real session at the end of it, and the right person's. §3.5 step (3) is reached by a principal
        // whose password and authenticator were both replaced en route, through three cookie writes.
        ILocator sessionName = terminal.Locator("span.session-name").First;
        await sessionName.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Equal(kitchenAccount.Username, (await sessionName.InnerTextAsync()).Trim());

        await terminal
            .Locator("nav.app-session a.session-link[href='/kitchen']")
            .First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // (g) Released. Without this, "the passkey path hits the pipeline" is satisfied just as well by a
        // middleware that refuses passkey sessions permanently, which would be a far worse defect than
        // the one (e) is guarding against.
        //
        // Freshly signed in, so the cookie is built from the cleared flags rather than from a claim that
        // happens to have gone stale — and the account is TOTP-enrolled again by now, so this incidentally
        // re-states scenario 13's property on a re-enrolled secret.
        await AccountJourneys.SignInWithPasskeyAsync(device, kitchenAccount.Username);

        Assert.Equal("/", new Uri(device.Url).AbsolutePath);
        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, device.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountRoutes.ForcedTotpEnrollment, device.Url, StringComparison.Ordinal);

        // (h) And the surface the reset was ordered from agrees the account is whole again — read from the
        // administrator's own browser, which has watched none of the last two minutes and is answering
        // from the row rather than from any document the staff member's screens are holding.
        ManagedAccount restored = await AdministrationJourneys.ReadAccountFactsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.Equal("Active", Assert.Single(restored.StatusChips));
        Assert.Contains("Password", restored.Credentials);
        Assert.Contains("Authenticator", restored.Credentials);
    }

    // -------------------------------------------------------------------------------------------
    // 16. An administrator works §11.4's administration surfaces from a 375×667 handset: no page
    //     scrolls sideways, every row's way in, every filter's submit and every detail form's button
    //     lies inside the screen, and every control is 44px tall. Ten surfaces since Slice 34 — six
    //     indexes and four detail pages — which is every §11.4 surface but the one that needs a sitting.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Administration_IsOperableOnAHandheldViewport()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(handheld: true, cancellationToken: cancellationToken);
        IPage page = instance.Page;

        // (a) An administrator, made on the phone, from the first screen this software ever shows.
        // The wizard is walked at 375px rather than arranged around, because a layout barrier that
        // only applies once somebody has signed in has a hole in the one place a new operator starts.
        await AccountJourneys.CompleteSetupAsync(page, AccountJourneys.DefaultAdministrator);

        // (b) Something in each list, because an empty list satisfies every assertion below and means
        // nothing (F-41). One extra account so the people index has two rows: a single-row list cannot
        // fail an assertion that only the widest row would fail, and it is a *row* that F-59 was about.
        StaffAccount counter = await AdministrationJourneys.CreateStaffAccountAsync(
            page, HandheldCounterUsername, HandheldCounterDisplayName, StaffRoles.Counter);

        Assert.Equal(HandheldCounterUsername, counter.Username);

        // The identifiers are kept as of Slice 34, because the four detail surfaces below are
        // `/…/{identifier}` routes and this is where the only identifiers this scenario will ever hold
        // are minted. Read back off each surface's own success panel rather than invented — see
        // AdministrationJourneys.CreateTableAsync for why that matters.
        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(page, HandheldTableLabel);
        MenuItemOnTheMenu menuItem = await AdministrationJourneys.CreateMenuItemAsync(
            page, HandheldMenuItemName, HandheldMenuItemPrice);

        // (c) Measure each surface once — six indexes, then the four detail surfaces whose identifiers
        // this scenario holds. Two of the six indexes are measured with nothing in their list, and that
        // is stated rather than glossed: opening a sitting needs a guest, a token and a join, and hiding
        // an order needs all of that plus an order and a close — scenario 3's and scenario 11's subjects,
        // not this one's. Both pages still have to lay out, and hidden-records' filter is the same §11.12
        // vocabulary the event explorer renders and is measured — so what is untested on those two is a
        // *row*, not the page. The display roster is measured the same way, with no display ever paired:
        // pairing one needs a second browser context and §4.2's whole ceremony, which is scenario 6.
        List<HandheldReachReport> reports = [];

        foreach (string path in HandheldAdministrationIndexPaths)
        {
            reports.Add(await HandheldReach.MeasureAsync(page, path));
        }

        foreach (string path in HandheldDetailPaths(
            counter.PersonIdentifier, tableIdentifier, menuItem.Identifier))
        {
            reports.Add(await HandheldReach.MeasureAsync(page, path));
        }

        // (d) The viewport is the one this scenario claims. First, and on its own, because every number
        // below is relative to it: if the context were laid out at Playwright's default 1280 the whole
        // scenario would pass and assert nothing. Read from the document rather than from the option
        // that set it — and compared as a ceiling with a scrollbar's allowance under it, because
        // `clientWidth` excludes a classic scrollbar and headless Chromium draws one on a page that
        // scrolls vertically, which every one of these does.
        foreach (HandheldReachReport report in reports)
        {
            Assert.True(
                report.ClientWidth <= RestaurantInstance.HandheldViewportWidth
                    && report.ClientWidth >= RestaurantInstance.HandheldViewportWidth - ScrollbarAllowancePixels,
                $"{report.Path} was measured in a {report.ClientWidth}px viewport, and this scenario is"
                    + $" about {RestaurantInstance.HandheldViewportWidth}px. Either the context was not"
                    + " created handheld, or something resized it — and at any wider width every"
                    + " assertion below passes on a page nobody claims is reachable.");
        }

        // (e) Enough was measured to be measuring something. What is counted is stated as a RULE rather
        // than as a census, and that is F-91: the census that used to stand here said fifteen and
        // itemised "a rename and a reprice on the item", while `ManageMenuItem` had carried four
        // `.manage-inline-form` blocks since Slice 38 and carries five since the section picker. A count
        // of rendered controls is a fact about ten surfaces and the rows this scenario happened to
        // arrange, written where nothing can check it — F-77's category exactly, and it went stale in the
        // slice that added a form without anyone noticing, because a floor that passes at fifteen also
        // passes at seventeen.
        //
        // The rule: every `.record-actions` and `.page-head-action` control on the six indexes, every
        // `.filter-actions` submit on the two read-only explorers, and every `.manage-inline-form` button
        // on the four detail surfaces. The floor is what makes the verdicts below mean anything, and it
        // is set under the smallest selector group rather than under the total — `.filter-actions`
        // contributes exactly two controls, one per explorer, so a rename of that class is the smallest
        // loss this floor has to survive and still catch.
        //
        // THE RESIDUAL IS NAMED. A floor cannot notice a group that grew, only one that vanished, so this
        // stays a non-vacuity guard rather than a census. Making it a census honestly would mean
        // attributing each measured control to the selector that matched it, which `HandheldReachReport`
        // does not carry — a real gate, deliberately not built in the slice that found the defect.
        int measured = reports.Sum(report => report.MeasuredCount);

        Assert.True(
            measured >= MinimumControlsMeasured,
            $"Only {measured} control(s) were measured across {reports.Count} surfaces, which is under the"
                + " floor. A selector this barrier reads has been renamed, or a page lost its list —"
                + " either way the assertions below are true of nothing.");

        // (f) F-59, as the number it always was. A page wider than its own viewport is a page an
        // operator reaches the far column of by dragging sideways, which is exactly what was reported.
        string[] sideways = reports
            .Where(report => report.ScrollsSideways)
            .Select(report => report.DescribeOverflow())
            .ToArray();

        Assert.True(
            sideways.Length == 0,
            "§11.12: an administration surface must not scroll sideways on the screen it is used from."
                + $" {string.Join(" · ", sideways)}");

        // (g) And the finding itself, per control: the way into a row is on the screen.
        MeasuredControl[] outOfReach = reports.SelectMany(report => report.OutOfReach).ToArray();

        Assert.True(
            outOfReach.Length == 0,
            "§11.12: a row's action is the full width of the foot of its card, so its box lies inside"
                + $" the viewport. Off the screen: {HandheldReach.Format(outOfReach)}. This is F-59, and"
                + " a control that has moved back into a right-hand column is how it returns.");

        // (h) The other half of §11.12's control rule, which no text assertion can reach either: a
        // target a finger can hit. `--touch-target` is 2.75rem and every control declares it, so a
        // failure here means a page overrode the declaration or invented a control without one.
        MeasuredControl[] undersized = reports.SelectMany(report => report.Undersized).ToArray();

        Assert.True(
            undersized.Length == 0,
            $"§11.12: every control is at least {HandheldReach.MinimumTouchTargetPixels}px tall."
                + $" Shorter: {HandheldReach.Format(undersized)}.");
    }

    // -------------------------------------------------------------------------------------------
    // 17. An administrator names two headings and puts a described item under each; a guest at a table
    //     reads the menu grouped under those headings, in the order the administrator chose, with the
    //     description on the card and in the detail panel. Then a heading is switched off and the guest's
    //     menu loses it — §7's rule that an inactive SECTION is hidden from the guest, which is the
    //     opposite of the rule for an inactive item, and the one thing about `0005` no unit test can see.
    //     Finally an item is refiled from one heading to the other and the guest watches it change
    //     groupings, landing at the END of its new heading — the last verb of the enhancement, and the
    //     only place §7's append-on-move rule is observed through a browser.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Guest_ReadsTheMenuGroupedUnderItsHeadings()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Seventeen";
        const string starters = "Starters";
        const string puddings = "Puddings";
        const string soupDescription = "Lentil and smoked paprika, with sourdough.";
        const string pieDescription = "Bramley apple, short crust, served warm.";

        GuestAccount guestAccount = new("e2e.menu.reader", "Menu Reader");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        // (a) Two headings, created in this order, so the menu's order is one somebody chose rather than
        // the alphabet's — "Puddings" sorts before "Starters" and the assertion below would pass by
        // accident if this read alphabetically. §7 appends each new section at MAX + 1.
        Guid startersIdentifier = await AdministrationJourneys.CreateMenuSectionAsync(
            administrator, starters, "Something to begin with.");
        Guid puddingsIdentifier = await AdministrationJourneys.CreateMenuSectionAsync(
            administrator, puddings);

        Assert.NotEqual(startersIdentifier, puddingsIdentifier);

        // (b) One described item under each. The description is the column `0004` shipped and nothing
        // read end to end until now — Slice 39 built the card that can show it and said so in its own
        // "what was NOT verified". This is that gap closed.
        MenuItemOnTheMenu soup = await AdministrationJourneys.CreateMenuItemAsync(
            administrator, "Soup of the day", 6.50m, soupDescription, starters);
        MenuItemOnTheMenu pie = await AdministrationJourneys.CreateMenuItemAsync(
            administrator, "Apple pie", 5.00m, pieDescription, puddings);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        // (c) The headings, in the order they were created. This is the assertion the whole scenario is
        // for: nothing below the guest surface can tell whether a heading was RENDERED, and §11.1's
        // grouping is an outer loop that a passing unit test would not have noticed the absence of.
        IReadOnlyList<MenuCard> menu = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count == 2,
            InteractivityPatience,
            "both menu items",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        // (d) Each card under its own heading, with its own sentence. Read off the card rather than
        // compared against what was typed in the form and back again: the description travels form →
        // menu_item.description → menu_item_event → guest surface, and this is the only assertion in the
        // project that crosses all four.
        MenuCard soupCard = Assert.Single(menu, card => card.Name == soup.Name);
        MenuCard pieCard = Assert.Single(menu, card => card.Name == pie.Name);

        Assert.Equal(starters, soupCard.SectionName);
        Assert.Equal(puddings, pieCard.SectionName);
        Assert.Equal(soupDescription, soupCard.Description);
        Assert.Equal(pieDescription, pieCard.Description);

        // (e) And in the detail panel, which is the surface's answer to "see more about that item if such
        // information exists". Asserted separately from the card because they are two elements: a panel
        // that rendered the chosen item's NAME with somebody else's description would satisfy (d).
        await TableOrderJourneys.ChooseAsync(guest, soup);

        ChosenItemDetail? detail = await TableOrderJourneys.ReadChosenItemDetailAsync(guest);

        Assert.NotNull(detail);
        Assert.Equal(soup.Name, detail.Name);
        Assert.Equal(soupDescription, detail.Description);

        // (f) A new item under an EXISTING heading joins that heading rather than starting a second one,
        // and it lands at the end of it. This is the half of `0005` that a scenario can see and a unit
        // test cannot: `DapperMenuAdministration` assigns MAX + 1 within the section under a lock, and
        // what proves the number means something is a guest reading two dishes in the order they were
        // put there rather than in the alphabet's ("Apple soup" sorts before "Soup of the day").
        MenuItemOnTheMenu second = await AdministrationJourneys.CreateMenuItemAsync(
            administrator, "Apple soup", 6.00m, sectionName: starters);

        IReadOnlyList<MenuCard> grown = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count == 3,
            InteractivityPatience,
            "the third item to arrive on the open menu",
            cancellationToken);

        // Still two headings: the new item joined Starters instead of creating a third grouping.
        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        string[] startersInOrder = grown
            .Where(card => card.SectionName == starters)
            .Select(card => card.Name)
            .ToArray();

        Assert.Equal(new[] { soup.Name, second.Name }, startersInOrder);

        // (g) §7's ASYMMETRY, end to end at last. An inactive SECTION is not rendered to the guest at
        // all — the opposite of the rule for an inactive item, which stays visible and marked one line
        // above. This assertion was drafted for Slice 40 and cut from it, and the cut was recorded rather
        // than made quietly: it needs SetMenuSectionActiveAsync to have a surface, and the section editor
        // was deliberately not in that slice. Asserting it then would have meant either a harness reaching
        // past the UI, which §16.3 refuses, or a verb wired for a test, which is worse.
        //
        // It arrives through the editor's own form, so it also exercises the §9 broadcast the fourth
        // section verb now publishes: nothing is clicked on the guest's page, and the heading has to
        // disappear from a circuit that was already open.
        await AdministrationJourneys.SetMenuSectionVisibilityAsync(
            administrator, puddingsIdentifier, visibleToGuests: false);

        IReadOnlyList<MenuCard> withoutPuddings = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.All(card => card.SectionName != puddings),
            InteractivityPatience,
            $"the '{puddings}' heading to leave the menu",
            cancellationToken);

        // The whole heading is gone, not merely its label: the pie went with it.
        Assert.Equal(new[] { starters }, withoutPuddings.Select(card => card.SectionName).Distinct().ToArray());
        Assert.DoesNotContain(withoutPuddings, card => card.Name == pie.Name);

        // And the items under it were NOT deactivated, which is the half of §7 that a passing assertion
        // above would not have distinguished. Starters is untouched — same two cards, same order, both
        // still orderable — so nothing cascaded downward. The administrator's own view still lists the pie
        // under its heading, because §11.4 sees every heading including the ones no guest can reach.
        Assert.Equal(
            new[] { soup.Name, second.Name },
            withoutPuddings.Where(card => card.SectionName == starters).Select(card => card.Name).ToArray());
        Assert.All(withoutPuddings, card => Assert.True(card.IsAvailable));

        // (h) And back. Reactivating restores the menu exactly as it was — same headings, same order,
        // same three cards — which is the property §7 states as "reactivating the heading brings the menu
        // back exactly as it was". A flip that had cascaded to the items would come back with the pie
        // marked unavailable, and this is the assertion that would say so.
        await AdministrationJourneys.SetMenuSectionVisibilityAsync(
            administrator, puddingsIdentifier, visibleToGuests: true);

        IReadOnlyList<MenuCard> restored = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count == 3,
            InteractivityPatience,
            $"the '{puddings}' heading to return to the menu",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        MenuCard restoredPie = Assert.Single(restored, card => card.Name == pie.Name);
        Assert.Equal(puddings, restoredPie.SectionName);
        Assert.Equal(pieDescription, restoredPie.Description);
        Assert.True(restoredPie.IsAvailable);

        // (i) And the last verb of the whole enhancement, end to end. An item is refiled from one heading
        // to another through `ManageMenuItem`'s own picker, and the guest — who has not touched anything —
        // watches the card change groupings, because §11.1 groups by heading and §9's MenuChanged is what
        // reaches an already-open circuit.
        //
        // WHERE IT LANDS IS THE ASSERTION. §7 appends a moved item to the END of its new heading, on the
        // same rule a created one follows, because a position belongs to the heading it is a position
        // within. Puddings already holds the pie at 0, so the soup must arrive behind it at 1 — and an
        // implementation that carried the item's old position across would put it at 1 as well here by
        // coincidence, which is why the ORDER is asserted rather than the number: `second` was at
        // position 1 in Starters and must still read second in Puddings, behind a pie it has never
        // shared a heading with.
        await AdministrationJourneys.MoveMenuItemToSectionAsync(
            administrator, second.Identifier, puddingsIdentifier);

        IReadOnlyList<MenuCard> refiled = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Any(card => card.Name == second.Name && card.SectionName == puddings),
            InteractivityPatience,
            $"'{second.Name}' to move under the '{puddings}' heading",
            cancellationToken);

        // Still two headings in the same order: a refile moves a card, it does not invent a grouping.
        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        // Starters keeps what it kept, and only that.
        Assert.Equal(
            new[] { soup.Name },
            refiled.Where(card => card.SectionName == starters).Select(card => card.Name).ToArray());

        // Appended: behind the pie, not beside it and not in front of it.
        Assert.Equal(
            new[] { pie.Name, second.Name },
            refiled.Where(card => card.SectionName == puddings).Select(card => card.Name).ToArray());

        // Nothing else about the item moved. §7 refiles a dish; it does not re-describe it, reprice it or
        // 86 it — and a move that had cascaded into any of those would show here rather than in a unit
        // test, because this is the only place the guest's own reading of the card is compared.
        MenuCard refiledCard = Assert.Single(refiled, card => card.Name == second.Name);
        Assert.True(refiledCard.IsAvailable);
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
    /// An amount as the application would have rendered it, through the application's own formatter and
    /// the currency code the instance was configured with (<see cref="RestaurantInstance.CurrencyCode"/>).
    ///
    /// <para>There are two ways to assert on money here and only one of them says anything. Writing
    /// <c>"$11.00"</c> hard-codes a claim about <c>RESTAURANT_CURRENCY_CODE</c> that silently becomes a
    /// claim about nothing the day it moves; formatting it the way the surface did makes the assertion be
    /// about the adjustment. Comparing formatted strings is also stricter than comparing decimals would
    /// be, because it catches a formatter that has started dropping its symbol — which §13 says is
    /// display-only and therefore has no other test above it.</para>
    /// </summary>
    private static string Money(decimal amount)
        => MoneyText.Format(amount, RestaurantInstance.CurrencyCode);

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
