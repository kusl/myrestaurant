using Microsoft.Playwright;
using MyRestaurant.EndToEnd.Tests.Harness;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

/// <summary>
/// §16.3 scenario <b>21</b>: a guest at a table says they like a dish, and the opinion is still there
/// after the page is reloaded (TECHNICAL_SPECIFICATION §7, §9, §11.1, §16.3; Stage 5b of
/// <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
///
/// <para><b>The reload is the scenario.</b> Everything before it — the control renders, it reports
/// unpressed, pressing it reports pressed — is satisfied exactly as well by a <c>bool</c> field on a
/// Blazor component that no database ever hears about. That implementation is not a straw man: it is
/// what "make the heart fill in when you tap it" produces, it is smaller than the real one, and every
/// unit fact and every other scenario in this repository stays green against it. Reloading destroys the
/// circuit and every field on it, so the second reading can only come from <c>0008</c>'s fold.</para>
///
/// <para><b>Why it exists in the slice that built the control rather than a slice later.</b> This
/// project deferred a picture scenario four times, each time with a recorded and reasonable-sounding
/// reason, and the cost was <b>F-106</b>: the upload committed, the redirect landed on a page that
/// answered HTTP 500, every administrator view of a decorated dish was broken including the one carrying
/// the Remove button, and eleven hundred unit facts, every integration fact and seventeen scenarios were
/// green throughout. The operator found it. A like control has the identical profile — an interactive
/// island, a circuit event, a toggle that looks right in source — so the scenario ships with the
/// control. That is <b>F-109</b>'s ruling applied on its first opportunity: a claim used to defer is
/// re-checked each time it is used again.</para>
///
/// <para><b>Its own class rather than a method on <c>EndToEndScenarios</c></b>, on
/// <see cref="MenuPictureScenarios"/>' precedent: that file names scenarios by number in a great many
/// places and is approaching three thousand lines. The numbering continues from twenty because the
/// matrix is one matrix; <c>RestaurantHarness</c> is a class fixture, so this class holds its own and
/// mints its own instances, and nothing is shared between them.</para>
///
/// <para><b>It seats its guest through <see cref="TableJourneys.SeatGuestAsync"/></b>, which moved into
/// the harness in this slice for exactly this reason. It was <c>private static</c> inside the other
/// scenario file, and a private method cannot be called from a second one — so the choice was to move it
/// or to paste it, and pasting a journey is F-59's mechanism with F-100's ruling already written down
/// against it.</para>
///
/// <para><b>Its guest sits at 375×667, and the scenario closes with §11.12's barrier (M6 Slice 64,
/// Stage 1d).</b> That is a second subject in one scenario and it is here rather than in a scenario 22
/// for the reason Slices 59, 60 and 61 each gave and which is now this project's default: <b>the
/// arrangement already exists</b>. A barrier over §11.1 wants a menu with an available dish and a
/// refused one, a way-in control beside the refused card, a panel open on it with a like inside, and a
/// staged line so the basket has controls — which is this scenario's closing state plus one
/// <c>StageAsync</c>. A scenario 22 would have cost a second container, a second passkey registration
/// and a second join to arrange what is already standing.</para>
///
/// <para><b>The two subjects have unrelated failure modes</b>, which is what satisfies the "one change,
/// one green run" rule here: a like that does not survive a reload is a fold reading the wrong row, and
/// a control under 44px is a stylesheet. Neither can be mistaken for the other in a failure message, and
/// both name the surface they are about.</para>
/// </summary>
public sealed class MenuReactionScenarios : IClassFixture<RestaurantHarness>
{
    private readonly RestaurantHarness _harness;

    /// <summary>
    /// How long a circuit is given to start, and how long a press is given to come back. The same figure
    /// <c>EndToEndScenarios</c> uses, and it is generous for the same reason: a cold instance is starting
    /// a container, a runtime and a WebSocket, and a scenario that timed out on the arrangement would
    /// report a defect in the thing it was about to test.
    /// </summary>
    private static readonly TimeSpan InteractivityPatience = TimeSpan.FromSeconds(30);

    public MenuReactionScenarios(RestaurantHarness harness) => _harness = harness;

    // -------------------------------------------------------------------------------------------
    //  21. A guest likes a dish, and the opinion outlives the circuit.
    //
    //      Four claims, in the order a failure is cheapest to diagnose:
    //
    //      (a) The control is in the DETAIL PANEL and not on the card. Asserted by there being no
    //          control to read until an item is chosen — which is also what makes the card safe, since
    //          a <button> inside a <button> is markup a parser silently splits and the half holding
    //          the dish's name would stop staging anything.
    //      (b) Pressing it reports pressed.
    //      (c) It SURVIVES A RELOAD. See the class summary: this is the only step a bool on the
    //          component would fail.
    //      (d) It is about a DISH and not about the surface — the other item, chosen straight after,
    //          reports unpressed. A write keyed on the wrong column, or a read that answered "has this
    //          person liked anything", passes (b) and (c) and fails here.
    //      (e) Unliking is written too, and also survives a reload. A verb that appended only 'liked'
    //          rows passes every step above: the fold would answer from the last row it wrote, which
    //          would be the like.
    //      (f) §11.4 READS THE SAME EVENT BACK — one like against the salmon and none against the
    //          pudding while the press stands, and none against the salmon once it is withdrawn. This
    //          is the only place in the repository where §11.1's write and §11.4's read meet, and it
    //          is what makes "two reads over one fold" a fact rather than a design note. A count over
    //          'liked' EVENTS rather than over current opinions passes every step before it.
    //
    //      (g) A DISH THAT IS OFF CAN STILL BE LIKED (Stage 5c). The kitchen 86s the salmon, the
    //          guest's open menu marks the card unavailable, and the panel is reached through the
    //          second control beside it rather than through the card §7 disabled. This is the gap
    //          Stage 5b-i wrote down instead of repairing, and nothing but a browser can say it is
    //          closed: the unit facts assert where the markup is, not that a browser answers it.
    //      (h) The panel reports the dish as unavailable, which is a branch no scenario had reached
    //          before — and reads the term the markup DECLARES rather than the one app.css paints,
    //          which is F-113 and makes this the first caller ChosenItemDetail.Facts has ever had.
    //      (i) The press writes, and (j) §11.4's count sees it. A surface that had merely opened a
    //          panel and toggled a field passes every step before (j).
    //
    //      No count is on the GUEST's screen to read, and that is Stage 5a's ruling rather than
    //      something not yet built. MenuItemReactionSurfaceContractTests holds it as a fact over the
    //      whole guest directory — and holds the mirror of it over §11.4's index, which must read the
    //      whole-menu count and never one person's presses — in two seconds rather than in a browser.
    //
    //      (k) ONE STAGED LINE, which is arrangement rather than assertion: it is what makes the
    //          basket's controls and an enabled Send exist for the barrier below.
    //      (l) §11.12 AT 375px, ON §11.1 (Stage 1d). The guest's context has been handheld since the
    //          join. Nothing in this repository had ever laid this surface out at the width it is read
    //          at — Stage 1 measured the surfaces STAFF use, because F-59 was found there, while R§1's
    //          sentence about a guest's own phone is what the whole section is justified by.
    //      (m) THE FONT FLOOR, AND IT IS F-118. The basket's quantity box is a bare <input> in a
    //          <label class="order-basket-quantity">, which is not a `.form-field` — so it had neither
    //          of §11.12's two halves and had not since the basket was written. A computed style read
    //          off a rendered element is the only instrument here that can see it: a text gate can
    //          assert that `app.css` declares the floor and cannot know which elements a page renders
    //          outside the arrangement it declares it against. F-66's shape a second time.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Guest_LikesADish_AndTheOpinionSurvivesAReload()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Twenty One";
        GuestAccount guestAccount = new("e2e.guest.likes", "Opinionated Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        // Two dishes, with names that are not substrings of one another, so that step (d) compares two
        // cards rather than two readings of one.
        MenuItemOnTheMenu salmon =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "E2E Grilled Salmon", 24.00m);
        MenuItemOnTheMenu pudding =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "E2E Sticky Toffee", 7.50m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        // THE GUEST IS ON A PHONE, and that is Stage 1d rather than decoration. Every assertion in
        // steps (a) to (j) is a DOM read or a click, and Playwright scrolls an element into view before
        // pressing it, so none of them can tell what width the context was laid out at — which is why
        // this costs one boolean and why it is safe to add to a scenario that already passes. What it
        // buys is step (k): the barrier at the end measures the surface this scenario has spent its
        // whole length arranging, and there was no cheaper way to arrange it.
        IPage guest = await TableJourneys.SeatGuestAsync(
            instance,
            tableIdentifier,
            joinSecret,
            guestAccount,
            InteractivityPatience,
            cancellationToken,
            handheld: true);

        // (a) Nothing to press until something is chosen. §11.1 renders the control inside the detail
        //     panel, and the panel renders only for a chosen item.
        // HasValue rather than Assert.Null: the reader returns bool?, and "there is no control" is a
        // different claim from "the control reports false" — conflating them is exactly the mistake a
        // control moved onto the card would hide.
        Assert.False(
            (await TableOrderJourneys.ReadChosenItemLikedAsync(guest)).HasValue,
            "§11.1 rendered a like control before any item was chosen. It belongs inside the detail"
            + " panel: on the card it would be a <button> inside a <button>, which a parser splits.");

        await TableOrderJourneys.ChooseAsync(guest, salmon);

        // A fresh guest likes nothing. Asserted rather than assumed, because every later step is a
        // CHANGE from this value and a control that reported "true" on arrival would make the press
        // below unfalsifiable.
        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        // (b) The press.
        Assert.True(await TableOrderJourneys.PressLikeAsync(guest, InteractivityPatience));

        // (c) The reload. The circuit and every field on it are gone; what comes back is read from
        //     menu_item_reaction_current.
        await ReopenTheMenuAsync(guest, salmon);

        Assert.True(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        // (c2) THE TWO READS OVER ONE FOLD MEET, and this is the only place in the repository where
        //      they do. §11.1 asked "which of these do I like" and §11.4 asks "which of these is
        //      popular"; they are different queries against the same rows, written by different people
        //      on different surfaces, and nothing but a browser can say that the guest's press and the
        //      staff's number are the same event. The pudding is asserted alongside, because "the count
        //      is 1" is also what a page hard-wired to report 1 would say.
        Assert.Equal(1, await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, salmon.Identifier));
        Assert.Null(await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, pudding.Identifier));

        // (d) The opinion is about a dish. Choosing the other one re-renders the same panel with the
        //     other item's state in it.
        await TableOrderJourneys.ChooseAsync(guest, pudding);

        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        // (e) Withdrawing it. Back to the salmon, press again, and reload once more — because a verb
        //     that only ever appended 'liked' would pass everything above.
        await TableOrderJourneys.ChooseAsync(guest, salmon);

        Assert.True(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));
        Assert.False(await TableOrderJourneys.PressLikeAsync(guest, InteractivityPatience));

        await ReopenTheMenuAsync(guest, salmon);

        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        // (e2) And the count comes back DOWN. A fold that answered from the earliest row rather than
        //      the latest, or a count over 'liked' events rather than over current opinions, passes
        //      every step up to here and fails on this one — which is the failure mode the data-access
        //      layer's own summary names as the plausible wrong implementation. Null rather than zero,
        //      because §11.4's read lists what is liked instead of left-joining the menu.
        Assert.Null(await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, salmon.Identifier));

        // (g) THE DISH GOES OFF, AND THE OPINION IS STILL AVAILABLE TO HAVE. Stage 5b-i recorded the
        //     gap and declined to repair it: §11.1 puts the like in the detail panel, the panel opens
        //     only for a chosen item, and §7 renders a deactivated item's card `disabled` — so "the
        //     salmon is off tonight and it is still the best thing here" was an opinion this surface
        //     could not record. Stage 5c is the repair, and this is the only thing in the repository
        //     that can say it worked: §16.1 rules out bUnit, so nothing else renders the control, and
        //     the unit facts can assert where the markup is and not that a browser will answer it.
        //
        //     The 86 goes through the KITCHEN's panel rather than the item's own page, on the harness's
        //     standing preference: both reach the same write and the same MenuChanged (§9), and this
        //     one does not need §11.4's editor open.
        await KitchenJourneys.OpenAsync(administrator, InteractivityPatience);
        await KitchenJourneys.EightySixAsync(administrator, salmon.Name);

        // Waited for rather than assumed, for the reason every menu wait in this suite is: the flip
        // travels as a broadcast to a circuit nobody is touching, and reading once would be sampling a
        // race. What is waited for is the CARD being refused, which is §7's half of the rule and the
        // thing that makes the next step non-trivial.
        await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Any(card =>
                card.Name.Contains(salmon.Name, StringComparison.Ordinal) && !card.IsAvailable),
            InteractivityPatience,
            "the salmon marked unavailable on the guest's open menu",
            cancellationToken);

        // The salmon's panel is still open from step (e) — _pickedMenuItemIdentifier is component state
        // and going off the menu does not clear it. Choosing the pudding closes it, and that is the
        // arrangement rather than a step: without it the next assertion would be satisfied by a panel
        // that had simply never gone away, which is the shape of a scenario proving nothing.
        await TableOrderJourneys.ChooseAsync(guest, pudding);

        ChosenItemDetail? puddingPanel = await TableOrderJourneys.ReadChosenItemDetailAsync(guest);

        Assert.NotNull(puddingPanel);
        Assert.Equal(pudding.Name, puddingPanel.Name);

        // (h) The way back in. ChooseAsync would refuse here and say why — §7 disabled that card — so
        //     the second control is the only route, which is exactly the claim.
        await TableOrderJourneys.InspectAsync(guest, salmon);

        ChosenItemDetail? salmonPanel = await TableOrderJourneys.ReadChosenItemDetailAsync(guest);

        Assert.NotNull(salmonPanel);
        Assert.Equal(salmon.Name, salmonPanel.Name);

        // The panel is showing the dish AS UNAVAILABLE, which is a branch no scenario had ever reached:
        // before Stage 5c the only way to see it was for MenuChanged to deactivate an item under an
        // already-open panel. Read by the term the markup declares rather than the one the stylesheet
        // paints — that is F-113, and this assertion is the first caller this dictionary has ever had.
        Assert.Equal("Not right now", salmonPanel.Facts["Available"]);

        // (i) And the like is reachable and writable. False first, because it was withdrawn in (e) and
        //     every claim below is a CHANGE from it.
        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));
        Assert.True(await TableOrderJourneys.PressLikeAsync(guest, InteractivityPatience));

        // (j) The write reached the database, not just the circuit — the same meeting of §11.1's write
        //     and §11.4's read that (c2) made, now for a dish that is off the menu. A surface that had
        //     opened a panel and toggled a field would pass everything above and fail here.
        Assert.Equal(1, await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, salmon.Identifier));

        // (k) THE LAST STEP OF THE ARRANGEMENT, AND IT IS THE BASKET. Nothing above this line puts a
        //     line in the basket, so `.order-basket-controls button` and an enabled Send do not exist
        //     — and the barrier below declares both REQUIRED, so leaving them unarranged fails loudly
        //     rather than measuring a smaller page. The pudding is what gets staged because §7 will
        //     not let the salmon be staged at all now: its card is disabled, which is the whole of
        //     step (g).
        await TableOrderJourneys.StageAsync(guest, pudding, quantity: 2);

        // Staging chose the pudding, which closed the salmon's panel. Reopening it through the way-in
        // control is what puts the surface into the state worth measuring: a refused card WITH its
        // sibling control beside it, a panel open on a dish that is off, and a like inside that panel.
        // That box model is the newest thing on this surface and the only one no browser has ever laid
        // out narrow.
        await TableOrderJourneys.InspectAsync(guest, salmon);

        // (l) §11.12 AT 375px, ON §11.1, FOR THE FIRST TIME. Stage 1 of the menu plan is "the handheld
        //     contract", and every slice of it measured the surfaces STAFF use, because F-59 was found
        //     there. Meanwhile R§1 — the sentence the whole section is justified by — is about the
        //     phone in a GUEST's hand, and this surface acquired headings, descriptions, a photograph,
        //     a detail panel, a like and a second control beside a refused card without one of them
        //     ever being laid out at the width they are read at.
        //
        //     Measured HERE rather than navigated to, which is the difference between this barrier and
        //     scenario 16's ten. Those are static-SSR pages and a GotoAsync is how you arrive at one.
        //     This surface is an interactive island: the chosen dish, the open panel and the staged
        //     line are circuit state, so navigating to it would destroy every one of them in order to
        //     look at them.
        HandheldReachReport report = await HandheldReach.MeasureHereAsync(
            guest,
            $"/table/{tableIdentifier:D}",
            HandheldSurface.GuestOrder);

        // The viewport is the one this step claims. First, and on its own, because every number below
        // is relative to it: at Playwright's default 1280 every assertion passes and means nothing.
        // Read from the document rather than from the option that set it, and compared as a ceiling
        // with a scrollbar's allowance under it — `clientWidth` excludes a classic scrollbar and
        // headless Chromium draws one on a page that scrolls vertically, which this one does.
        Assert.True(
            report.ClientWidth <= RestaurantInstance.HandheldViewportWidth
                && report.ClientWidth >= RestaurantInstance.HandheldViewportWidth - ScrollbarAllowancePixels,
            $"§11.1 was measured in a {report.ClientWidth}px viewport, and this step is about"
                + $" {RestaurantInstance.HandheldViewportWidth}px. Either the guest's context was not"
                + " created handheld, or something resized it — and at any wider width every assertion"
                + " below passes on a page nobody claims is reachable.");

        // No floor is asserted on the total, and that is deliberate rather than an omission. Every
        // selector in this surface's set is REQUIRED, so `MeasureHereAsync` has already refused a run
        // in which any of them matched nothing, naming the selector and printing the census. A total
        // floor here would be the weaker instrument the guest set no longer needs — and it is exactly
        // the residual scenario 16's own comment recorded as "a real gate, deliberately not built".
        Assert.NotEmpty(report.Reachable);

        // F-59, as the number it always was, on the surface F-59's own justification is about.
        Assert.False(
            report.ScrollsSideways,
            "§11.12: the guest's ordering surface must not scroll sideways on the screen it is read"
                + $" from. {report.DescribeOverflow()}. Census: {report.DescribeCensus()}.");

        // The finding itself, per control: everything a guest taps is on the screen.
        Assert.True(
            report.OutOfReach.Count == 0,
            "§11.12: a dish's card is the full width of the menu column and every other control on"
                + $" this surface lies inside the viewport. Off the screen:"
                + $" {HandheldReach.Format(report.OutOfReach)}.");

        // The touch-target half. `--touch-target` is 2.75rem and every control here declares it, so a
        // failure means a rule overrode the declaration or a control was written without one.
        Assert.True(
            report.Undersized.Count == 0,
            $"§11.12: every control is at least {HandheldReach.MinimumTouchTargetPixels}px tall."
                + $" Shorter: {HandheldReach.Format(report.Undersized)}.");

        // (m) THE FONT FLOOR, AND IT IS F-118. This is the half of §11.12's control rule that no text
        //     gate in this repository can reach: `HandheldLayoutContractTests` asserts that `app.css`
        //     DECLARES the floor, and whether a page put its <input> inside an arrangement that
        //     carries the declaration is a fact about markup. §11.1's basket had not — the quantity
        //     box beside "Take out" was a bare <input> in a <label class="order-basket-quantity">,
        //     matched by exactly one rule in the whole stylesheet (`max-width`), and therefore a
        //     user-agent default of roughly 13px in roughly 21px of height, on the one surface R§1
        //     says a guest reads from their own phone. iOS Safari zooms the viewport on a focused
        //     control under 16px and does not zoom back.
        //
        //     Asserted last because it is the assertion this scenario was extended to be able to make,
        //     and because it is the one that fails if the repair in `app.css` was written wrong.
        Assert.True(
            report.UndersizedText.Count == 0,
            $"§11.12: every text control is at least {HandheldReach.MinimumTextFontPixels}px."
                + $" Under it: {HandheldReach.Format(report.UndersizedText)}. This is F-118: the"
                + " control is rendered outside any arrangement `app.css` declares the floor against,"
                + " so it inherits a user-agent default and iOS Safari zooms the page around it.");
    }

    /// <summary>
    /// The slack allowed under <see cref="RestaurantInstance.HandheldViewportWidth"/> when the viewport
    /// is read back from the document. <c>document.documentElement.clientWidth</c> excludes a classic
    /// scrollbar, and headless Chromium draws one on any page that scrolls vertically — which §11.1
    /// does the moment there is a menu and a basket on it. The same figure and the same reason as
    /// <c>EndToEndScenarios</c>' constant of this name; it is deliberately not shared, because the two
    /// files are two scenario classes with their own fixtures and a constant reaching across them would
    /// be the first thing either shares.
    /// </summary>
    private const double ScrollbarAllowancePixels = 20.0;

    /// <summary>
    /// Reloads the guest's table page, waits for the circuit to come back, and chooses one dish again.
    ///
    /// <para><b>A reload rather than a fresh page</b>, because the cookie, the sitting and the membership
    /// are all supposed to survive it and a new context would re-arrange all three — the claim under test
    /// is about what the <em>server</em> stored, not about what a browser can be persuaded to do
    /// twice.</para>
    ///
    /// <para>Choosing again is necessary rather than incidental: <c>_pickedMenuItemIdentifier</c> is
    /// component state, so a reloaded surface has no item chosen and therefore no detail panel — which is
    /// the same fact step (a) asserts, arrived at from the other direction.</para>
    /// </summary>
    private static async Task ReopenTheMenuAsync(IPage guest, MenuItemOnTheMenu item)
    {
        await guest.ReloadAsync();
        await TableOrderJourneys.WaitForLiveSurfaceAsync(guest, InteractivityPatience);
        await TableOrderJourneys.ChooseAsync(guest, item);
    }

    private void SkipUnlessHarnessAvailable()
        => Assert.SkipUnless(
            _harness.SkipReason is null,
            _harness.SkipReason ?? "The end-to-end harness is unavailable.");
}
