using Microsoft.Playwright;
using MyRestaurant.EndToEnd.Tests.Harness;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

public sealed class MenuReactionScenarios : IClassFixture<RestaurantHarness>
{
    private readonly RestaurantHarness _harness;

    private static readonly TimeSpan InteractivityPatience = TimeSpan.FromSeconds(30);

    private const int WideFixtureEdge = 400;

    private const string FirstComment = "It is off tonight and it is still the best thing here.";

    private const string SecondComment = "Ask them to keep one back for me next time.";

    public MenuReactionScenarios(RestaurantHarness harness) => _harness = harness;

    [Fact]
    public async Task Guest_LikesADish_SaysWhatTheyThought_AndStaffReadTheSentence()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Twenty One";
        GuestAccount guestAccount = new("e2e.guest.likes", "Opinionated Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu salmon =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "E2E Grilled Salmon", 24.00m);
        MenuItemOnTheMenu pudding =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "E2E Sticky Toffee", 7.50m);

        int storedWidth = await MenuPictureJourneys.AttachAsync(
            administrator,
            salmon,
            PictureFixtures.SquareGradientPng(WideFixtureEdge),
            fileName: "salmon.png",
            mimeType: "image/png");

        Assert.Equal(WideFixtureEdge, storedWidth);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        IPage guest = await TableJourneys.SeatGuestAsync(
            instance,
            tableIdentifier,
            joinSecret,
            guestAccount,
            InteractivityPatience,
            cancellationToken,
            handheld: true);

        Assert.False(
            (await TableOrderJourneys.ReadChosenItemLikedAsync(guest)).HasValue,
            "§11.1 rendered a like control before any item was chosen. It belongs inside the detail"
            + " panel: on the card it would be a <button> inside a <button>, which a parser splits.");

        await TableOrderJourneys.ChooseAsync(guest, salmon);

        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        Assert.True(await TableOrderJourneys.PressLikeAsync(guest, InteractivityPatience));

        await ReopenTheMenuAsync(guest, salmon);

        Assert.True(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        Assert.Equal(1, await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, salmon.Identifier));
        Assert.Null(await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, pudding.Identifier));

        await TableOrderJourneys.ChooseAsync(guest, pudding);

        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        await TableOrderJourneys.ChooseAsync(guest, salmon);

        Assert.True(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));
        Assert.False(await TableOrderJourneys.PressLikeAsync(guest, InteractivityPatience));

        await ReopenTheMenuAsync(guest, salmon);

        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));

        Assert.Null(await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, salmon.Identifier));

        Assert.Equal(string.Empty, await TableOrderJourneys.ReadChosenItemCommentAsync(guest));

        Assert.Null(await AdministrationJourneys.ReadMenuIndexCommentAsync(
            administrator, salmon.Identifier));

        Assert.Equal(
            "Submitted",
            await TableOrderJourneys.SaveCommentAsync(
                guest, FirstComment + "   ", InteractivityPatience));

        Assert.Equal(FirstComment, await TableOrderJourneys.ReadChosenItemCommentAsync(guest));

        await ReopenTheMenuAsync(guest, salmon);

        Assert.Equal(FirstComment, await TableOrderJourneys.ReadChosenItemCommentAsync(guest));

        await TableOrderJourneys.ChooseAsync(guest, pudding);

        Assert.Equal(string.Empty, await TableOrderJourneys.ReadChosenItemCommentAsync(guest));

        await TableOrderJourneys.ChooseAsync(guest, salmon);

        Assert.Equal(FirstComment, await TableOrderJourneys.ReadChosenItemCommentAsync(guest));

        Assert.Equal(FirstComment, await AdministrationJourneys.ReadMenuIndexCommentAsync(
            administrator, salmon.Identifier));
        Assert.Equal(1, await AdministrationJourneys.ReadMenuIndexCommentCountAsync(
            administrator, salmon.Identifier));
        Assert.Null(await AdministrationJourneys.ReadMenuIndexCommentAsync(
            administrator, pudding.Identifier));

        Assert.Equal(
            "NoChange",
            await TableOrderJourneys.SaveCommentAsync(guest, FirstComment, InteractivityPatience));

        Assert.Equal(
            "Withdrawn",
            await TableOrderJourneys.WithdrawCommentAsync(guest, InteractivityPatience));

        await ReopenTheMenuAsync(guest, salmon);

        Assert.Equal(string.Empty, await TableOrderJourneys.ReadChosenItemCommentAsync(guest));

        Assert.Null(await AdministrationJourneys.ReadMenuIndexCommentAsync(
            administrator, salmon.Identifier));
        Assert.Null(await AdministrationJourneys.ReadMenuIndexCommentCountAsync(
            administrator, salmon.Identifier));

        Assert.Equal(
            "Submitted",
            await TableOrderJourneys.SaveCommentAsync(guest, SecondComment, InteractivityPatience));

        await KitchenJourneys.OpenAsync(administrator, InteractivityPatience);
        await KitchenJourneys.EightySixAsync(administrator, salmon.Name);

        await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Any(card =>
                card.Name.Contains(salmon.Name, StringComparison.Ordinal) && !card.IsAvailable),
            InteractivityPatience,
            "the salmon marked unavailable on the guest's open menu",
            cancellationToken);

        await TableOrderJourneys.ChooseAsync(guest, pudding);

        ChosenItemDetail? puddingPanel = await TableOrderJourneys.ReadChosenItemDetailAsync(guest);

        Assert.NotNull(puddingPanel);
        Assert.Equal(pudding.Name, puddingPanel.Name);

        await TableOrderJourneys.InspectAsync(guest, salmon);

        ChosenItemDetail? salmonPanel = await TableOrderJourneys.ReadChosenItemDetailAsync(guest);

        Assert.NotNull(salmonPanel);
        Assert.Equal(salmon.Name, salmonPanel.Name);

        Assert.Equal("Not right now", salmonPanel.Facts["Available"]);

        Assert.False(await TableOrderJourneys.ReadChosenItemLikedAsync(guest));
        Assert.True(await TableOrderJourneys.PressLikeAsync(guest, InteractivityPatience));

        Assert.Equal(1, await AdministrationJourneys.ReadMenuIndexLikeCountAsync(
            administrator, salmon.Identifier));

        await TableOrderJourneys.StageAsync(guest, pudding, quantity: 2);

        await TableOrderJourneys.InspectAsync(guest, salmon);

        Assert.Equal(
            WideFixtureEdge,
            await MenuPictureJourneys.WaitForDecodedAsync(
                guest,
                "#table-order-surface img.order-menu-thumbnail"));

        Assert.Equal(
            WideFixtureEdge,
            await MenuPictureJourneys.WaitForDecodedAsync(
                guest,
                "#table-order-surface img.order-menu-detail-picture"));

        HandheldReachReport report = await HandheldReach.MeasureHereAsync(
            guest,
            $"/table/{tableIdentifier:D}",
            HandheldSurface.GuestOrder);

        Assert.True(
            report.ClientWidth <= RestaurantInstance.HandheldViewportWidth
                && report.ClientWidth >= RestaurantInstance.HandheldViewportWidth - ScrollbarAllowancePixels,
            $"§11.1 was measured in a {report.ClientWidth}px viewport, and this step is about"
                + $" {RestaurantInstance.HandheldViewportWidth}px. Either the guest's context was not"
                + " created handheld, or something resized it — and at any wider width every assertion"
                + " below passes on a page nobody claims is reachable.");

        Assert.NotEmpty(report.Reachable);

        Assert.False(
            report.ScrollsSideways,
            "§11.12: the guest's ordering surface must not scroll sideways on the screen it is read"
                + $" from. {report.DescribeOverflow()}. Census: {report.DescribeCensus()}.");

        Assert.True(
            report.OutOfReach.Count == 0,
            "§11.12: a dish's card is the full width of the menu column and every other control on"
                + $" this surface lies inside the viewport. Off the screen:"
                + $" {HandheldReach.Format(report.OutOfReach)}.");

        Assert.True(
            report.Undersized.Count == 0,
            $"§11.12: every control is at least {HandheldReach.MinimumTouchTargetPixels}px tall."
                + $" Shorter: {HandheldReach.Format(report.Undersized)}.");

        Assert.True(
            report.UndersizedText.Count == 0,
            $"§11.12: every text control is at least {HandheldReach.MinimumTextFontPixels}px."
                + $" Under it: {HandheldReach.Format(report.UndersizedText)}. This is F-118: the"
                + " control is rendered outside any arrangement `app.css` declares the floor against,"
                + " so it inherits a user-agent default and iOS Safari zooms the page around it.");
    }

    private const double ScrollbarAllowancePixels = 20.0;

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
