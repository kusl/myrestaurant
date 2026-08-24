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
    //
    //      No count is read anywhere, and none is on screen to read: Stage 5a ruled the number
    //      staff-facing. MenuItemReactionSurfaceContractTests holds that as a fact over the whole
    //      guest directory, in two seconds rather than in a browser.
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

        IPage guest = await TableJourneys.SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, InteractivityPatience, cancellationToken);

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
    }

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
