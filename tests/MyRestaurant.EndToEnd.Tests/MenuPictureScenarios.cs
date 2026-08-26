using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.EndToEnd.Tests.Harness;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

public sealed class MenuPictureScenarios : IClassFixture<RestaurantHarness>
{
    private readonly RestaurantHarness _harness;

    private const int SmallPictureEdge = 12;

    private const int LargePictureEdge = 640;

    private const string FileInputSelector = MenuPictureJourneys.FileInput;

    private const string StatusSelector = MenuPictureJourneys.Status;

    private const string FlashSelector = MenuPictureJourneys.Flash;

    private const string ThumbnailSelector = MenuPictureJourneys.Thumbnail;

    private const string FactsSelector = MenuPictureJourneys.Facts;

    private const string AltTextSelector = MenuPictureJourneys.AltTextInput;

    private const string UnnamedContentType = "application/octet-stream";

    public MenuPictureScenarios(RestaurantHarness harness) => _harness = harness;

    [Fact]
    public async Task Administrator_AttachesAPictureAndThePageThatShowsItRenders()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string dish = "E2E Grilled Salmon";
        const string caption = "Skin side up on a bed of wilted greens.";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu item =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, dish, 18.50m);

        await administrator.GotoAsync($"/administration/menu/{item.Identifier:D}");

        Assert.Equal(0, await administrator.Locator(ThumbnailSelector).CountAsync());
        Assert.Contains(
            "No picture has ever been attached",
            await administrator.InnerTextAsync("body"),
            StringComparison.Ordinal);

        byte[] picture = PictureFixtures.SquareGradientPng(SmallPictureEdge);

        await administrator.SetInputFilesAsync(
            FileInputSelector,
            new FilePayload
            {
                Name = "salmon.png",
                MimeType = "image/png",
                Buffer = picture,
            });

        await administrator.Locator(StatusSelector).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        Assert.Contains(
            "stored exactly as it is",
            await administrator.InnerTextAsync(StatusSelector),
            StringComparison.Ordinal);

        await administrator.ClickAsync("button:has-text('Attach picture')");

        await administrator.Locator(FlashSelector).WaitForAsync(
            new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Contains(
            "Picture attached",
            await administrator.InnerTextAsync(FlashSelector),
            StringComparison.Ordinal);

        ILocator thumbnail = administrator.Locator(ThumbnailSelector);
        await thumbnail.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        int decodedWidth = await thumbnail.EvaluateAsync<int>("element => element.naturalWidth");
        Assert.Equal(SmallPictureEdge, decodedWidth);

        string facts = await administrator.InnerTextAsync(FactsSelector);
        Assert.Contains("image/png", facts, StringComparison.Ordinal);
        Assert.Contains(
            $"{picture.Length.ToString(CultureInfo.InvariantCulture)} bytes",
            facts,
            StringComparison.Ordinal);

        Assert.Contains(
            "Picture attached",
            await PictureHistoryAsync(administrator),
            StringComparison.Ordinal);

        await administrator.FillAsync(AltTextSelector, caption);
        await administrator.ClickAsync("button:has-text('Save caption')");

        await administrator.Locator(FlashSelector).WaitForAsync(
            new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Contains(
            "Caption saved",
            await administrator.InnerTextAsync(FlashSelector),
            StringComparison.Ordinal);

        Assert.Contains(
            caption,
            await PictureHistoryAsync(administrator),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administrator_ChoosesAPictureOverTheCapAndTheBrowserMakesItFit()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string dish = "E2E Bramley Pie";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu item =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, dish, 7.25m);

        await administrator.GotoAsync($"/administration/menu/{item.Identifier:D}");

        string? budget = await administrator
            .Locator(FileInputSelector)
            .GetAttributeAsync("data-picture-byte-budget");

        Assert.False(
            string.IsNullOrWhiteSpace(budget),
            "the file input carries no byte budget, so the browser-side downscaler is switched off and"
                + " this scenario would assert nothing about it. The page reads the cap from"
                + " pg_constraint at render time; a null there renders no attribute.");

        int declaredCap = int.Parse(budget!, CultureInfo.InvariantCulture);

        byte[] chosen = PictureFixtures.SquareGradientPng(LargePictureEdge);

        Assert.True(
            chosen.Length > declaredCap,
            $"the fixture picture is {chosen.Length} bytes against a declared cap of {declaredCap}, so"
                + " it would have been accepted unchanged and this scenario would prove nothing. Raise"
                + $" {nameof(LargePictureEdge)}.");

        await administrator.SetInputFilesAsync(
            FileInputSelector,
            new FilePayload
            {
                Name = "pie.png",
                MimeType = "image/png",
                Buffer = chosen,
            });

        await administrator.Locator(StatusSelector).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        string reported = await WaitForResizeReportAsync(administrator);

        Assert.Contains("Resized for the menu", reported, StringComparison.Ordinal);

        ILocator control = administrator.Locator(FileInputSelector);

        string heldType = await control.EvaluateAsync<string>("element => element.files[0].type");
        int heldSize = await control.EvaluateAsync<int>("element => element.files[0].size");

        Assert.Equal("image/jpeg", heldType);
        Assert.True(
            heldSize <= declaredCap,
            $"the browser produced {heldSize} bytes against a cap of {declaredCap}.");
        Assert.True(
            heldSize < chosen.Length,
            $"the browser produced {heldSize} bytes from a {chosen.Length}-byte original, which is not a"
                + " reduction.");

        await administrator.ClickAsync("button:has-text('Attach picture')");

        await administrator.Locator(FlashSelector).WaitForAsync(
            new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Contains(
            "Picture attached",
            await administrator.InnerTextAsync(FlashSelector),
            StringComparison.Ordinal);

        string facts = await administrator.InnerTextAsync(FactsSelector);
        Assert.Contains("image/jpeg", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("image/png", facts, StringComparison.Ordinal);

        ILocator thumbnail = administrator.Locator(ThumbnailSelector);
        await thumbnail.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        int decodedWidth = await thumbnail.EvaluateAsync<int>("element => element.naturalWidth");

        Assert.True(
            decodedWidth > 0 && decodedWidth <= LargePictureEdge,
            $"the stored picture decoded at {decodedWidth}px, which is not a resized {LargePictureEdge}px"
                + " original.");

        Assert.Contains(
            "Picture attached",
            await PictureHistoryAsync(administrator),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administrator_AttachesAPictureTheBrowserCouldNotName()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string dish = "E2E Potted Shrimp";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu item =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, dish, 9.75m);

        await administrator.GotoAsync($"/administration/menu/{item.Identifier:D}");

        byte[] picture = PictureFixtures.SquareGradientPng(SmallPictureEdge);

        await administrator.SetInputFilesAsync(
            FileInputSelector,
            new FilePayload
            {
                Name = "shrimp",
                MimeType = UnnamedContentType,
                Buffer = picture,
            });

        await administrator.Locator(StatusSelector).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        string held = await administrator
            .Locator(FileInputSelector)
            .EvaluateAsync<string>("element => element.files[0].type");

        Assert.Equal(UnnamedContentType, held);

        await administrator.ClickAsync("button:has-text('Attach picture')");

        await administrator.Locator(FlashSelector).WaitForAsync(
            new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Contains(
            "Picture attached",
            await administrator.InnerTextAsync(FlashSelector),
            StringComparison.Ordinal);

        string facts = await administrator.InnerTextAsync(FactsSelector);

        Assert.Contains("image/png", facts, StringComparison.Ordinal);
        Assert.DoesNotContain(UnnamedContentType, facts, StringComparison.Ordinal);

        ILocator thumbnail = administrator.Locator(ThumbnailSelector);
        await thumbnail.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        int decodedWidth = await thumbnail.EvaluateAsync<int>("element => element.naturalWidth");
        Assert.Equal(SmallPictureEdge, decodedWidth);
    }

    private static async Task<string> WaitForResizeReportAsync(IPage page)
    {
        await MenuPictureJourneys.SettleAsync(page);

        return await page.InnerTextAsync(StatusSelector);
    }

    private static async Task<string> PictureHistoryAsync(IPage page)
    {
        ILocator panel = page.Locator(".record-list").First;
        await panel.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return await panel.InnerTextAsync();
    }

    private void SkipUnlessHarnessAvailable()
        => Assert.SkipUnless(
            _harness.SkipReason is null,
            _harness.SkipReason ?? "The end-to-end harness is unavailable.");
}
