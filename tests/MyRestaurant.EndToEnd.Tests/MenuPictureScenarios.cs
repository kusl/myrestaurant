using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.EndToEnd.Tests.Harness;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

/// <summary>
/// §16.3 scenarios <b>18</b> and <b>19</b>: an administrator puts a photograph on a dish, and the browser
/// makes one that is too large fit (TECHNICAL_SPECIFICATION §7, §11.4, §16.3; Stage 4e).
///
/// <para><b>Why these exist, and it is not tidiness.</b> Four slices built this feature and no browser
/// had ever loaded a picture through it. The consequence arrived exactly as an unexercised path does:
/// <b>F-106</b> shipped a <c>ValidationMessage</c> one line outside its <c>EditForm</c>, inside
/// <c>@if (_picture is not null)</c>. The upload POST renders while <c>_picture</c> is still null, so it
/// succeeded, committed and redirected — and the GET it redirected to was the first render in which a
/// picture existed, and answered <b>500</b>. Every administrator view of a decorated item answered 500
/// from then on, including the one carrying the Remove button, so the state was not reversible from any
/// surface. Eleven hundred unit facts, every integration fact and seventeen §16.3 scenarios were green
/// throughout. The operator found it. <b>These two scenarios are the assertion that finding would have
/// failed</b>, and the second one drives the whole of Stage 4e as well.</para>
///
/// <para><b>Its own class rather than two more methods on <c>EndToEndScenarios</c>.</b> That file names
/// scenarios by number in a great many places and is approaching three thousand lines; a subject with two
/// scenarios, a fixture generator of its own and a browser-side mechanism nothing else in the matrix
/// touches is a file. The numbering continues from seventeen rather than restarting, because the matrix
/// is one matrix — <c>RestaurantHarness</c> is a class fixture, so this class holds its own harness and
/// mints its own instances exactly as the other one does, and nothing is shared between them.</para>
///
/// <para><b>What the fixture picture is and is not</b> — see <see cref="PictureFixtures"/>. The plan
/// deferred a picture scenario four times on the ground that inventing bytes would be a test arranging
/// what it asserts about. Nothing here asserts anything about the bytes: the claims are that an upload
/// round-trips, that the browser reduces one over §8.2's cap, and that the history records what
/// happened.</para>
///
/// <para>Both begin with <see cref="SkipUnlessHarnessAvailable"/>, on the same opt-in
/// (<c>MYRESTAURANT_E2E=1</c>) plus container engine plus Chromium plus current build that every other
/// scenario needs.</para>
/// </summary>
public sealed class MenuPictureScenarios : IClassFixture<RestaurantHarness>
{
    private readonly RestaurantHarness _harness;

    /// <summary>
    /// Comfortably inside §8.2's cap — a few hundred bytes — so scenario 18 is about the round trip and
    /// nothing else. The downscaler leaves it completely alone at this size, which is itself part of what
    /// that scenario asserts: a picture that already fits is stored exactly as it was chosen.
    /// </summary>
    private const int SmallPictureEdge = 12;

    /// <summary>
    /// Over the cap by more than a factor of two. The bytes are <c>edge × (1 + edge × 3)</c> of stored
    /// deflate, so this is a little over a megabyte against a cap of half of one — a real phone
    /// photograph is four, and the ratio is what matters rather than the absolute size, because the
    /// scenario has to pay for every byte of it through a Chromium file input.
    /// </summary>
    private const int LargePictureEdge = 640;

    /// <summary>The upload form's file input, its status line, and the panel the redirect lands on.</summary>
    private const string FileInputSelector = "#picture-file";

    private const string StatusSelector = "#picture-status";

    private const string FlashSelector = ".status-success";

    private const string ThumbnailSelector = "img.manage-picture-image";

    public MenuPictureScenarios(RestaurantHarness harness) => _harness = harness;

    // -------------------------------------------------------------------------------------------
    //  18. An administrator attaches a photograph to a dish, and the page that shows it renders.
    //
    //      The second clause is the scenario. Attaching worked before this slice — the row committed
    //      and the redirect was issued — and what did not work was the page the redirect landed on
    //      (F-106). So the assertions walk forward from the POST rather than stopping at it: the flash
    //      is read on the redirected GET, the thumbnail is required to have DECODED in the browser
    //      rather than merely to be present in the markup, and the picture history is required to carry
    //      the attach. Every one of those is a render of the block that used to throw.
    //
    //      The caption editor is then used, because it is the form F-106's ValidationMessage belongs to
    //      and the one whose validation could not previously be reached at all — and because a caption
    //      is the one thing on this page that changes a guest's menu without changing the picture's
    //      address.
    // -------------------------------------------------------------------------------------------
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

        // (a) The panel says there is no picture, and the history says there has never been one. Both
        // are the states this page could only describe from Stage 4d onwards, and asserting them here
        // is what makes the assertions after the upload mean something.
        Assert.Equal(0, await administrator.Locator(ThumbnailSelector).CountAsync());
        Assert.Contains(
            "No picture has ever been attached",
            await administrator.InnerTextAsync("body"),
            StringComparison.Ordinal);

        // (b) A real PNG through the real control. SetInputFilesAsync dispatches input and change, so
        // the downscaler runs on it exactly as it would for a person — and at this size it must decide
        // to do nothing, which the status line is required to say.
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

        // (c) The redirected GET. THIS is the request F-106 made answer 500, and the wait is on the
        // flash rather than on a navigation because a 500 also completes a navigation.
        await administrator.Locator(FlashSelector).WaitForAsync(
            new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Contains(
            "Picture attached",
            await administrator.InnerTextAsync(FlashSelector),
            StringComparison.Ordinal);

        // (d) The thumbnail DECODED, which is a stronger claim than the markup carrying a src: it means
        // §7's route answered, the stored content type was right for the stored bytes, and §11.11's
        // img-src admitted it. naturalWidth is zero for an image that failed to load.
        ILocator thumbnail = administrator.Locator(ThumbnailSelector);
        await thumbnail.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        int decodedWidth = await thumbnail.EvaluateAsync<int>("element => element.naturalWidth");
        Assert.Equal(SmallPictureEdge, decodedWidth);

        // Stored verbatim, which is the rule for anything already inside the cap: same format, same
        // byte count as what was handed to the control.
        string facts = await administrator.InnerTextAsync("figcaption.manage-picture-facts");
        Assert.Contains("image/png", facts, StringComparison.Ordinal);
        Assert.Contains(
            $"{picture.Length.ToString(CultureInfo.InvariantCulture)} bytes",
            facts,
            StringComparison.Ordinal);

        // (e) The picture history, which is the other half of the block that used to throw.
        Assert.Contains(
            "Picture attached",
            await PictureHistoryAsync(administrator),
            StringComparison.Ordinal);

        // (f) The caption editor — F-106's own form. Its ValidationMessage now lives inside it, so this
        // is also the first time anything has rendered that form on a page carrying a picture.
        await administrator.FillAsync("#picture-alt-text", caption);
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

    // -------------------------------------------------------------------------------------------
    //  19. A picture over §8.2's cap is made to fit by the browser, and the one that arrives is the
    //      smaller one.
    //
    //      This is the whole of Stage 4e and it is not assertable anywhere else in this repository.
    //      The downscaling happens in a <canvas> in a real browser, on a file chosen through a real
    //      input, and what proves it worked is not that a smaller file exists but that the SERVER
    //      stored a smaller one — so the closing assertions read the stored format and the stored byte
    //      count off §11.4's own panel, and require them to disagree with what was handed to the
    //      control.
    //
    //      The cap is never written in this file. What is asserted is the pair of inequalities that
    //      hold whatever the cap is: the chosen file was refused-size, the stored one is smaller than
    //      it, and the upload was not refused — which is the same shape of claim the file-size gate in
    //      MenuItemImageSurfaceContractTests makes about the number's location.
    // -------------------------------------------------------------------------------------------
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

        // (a) The control was handed a budget at all. Without this the rest of the scenario would
        // silently become a test that an oversized upload is refused — which is true, was true before
        // Stage 4e, and is not what this is about.
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

        // (b) Chosen through the real input. The change event runs the downscaler; the status line is
        // the only signal that it has finished, which is exactly why the surface has one.
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

        // (c) It is the JPEG the ladder produces that the control now holds, not the PNG that was
        // chosen. Read off the input itself, because this is the one moment where the browser's state
        // and the operator's file differ and the difference is the feature.
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

        // (d) Accepted, and accepted as the smaller file. The stored facts are read off §11.4's panel
        // because that is the application's own account of what it holds — a JPEG, and fewer bytes than
        // were chosen. Before Stage 4e this upload's only possible outcome was a refusal.
        Assert.Contains(
            "Picture attached",
            await administrator.InnerTextAsync(FlashSelector),
            StringComparison.Ordinal);

        string facts = await administrator.InnerTextAsync("figcaption.manage-picture-facts");
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

    /// <summary>
    /// The status line once the downscaler has stopped working, rather than the first thing it says.
    ///
    /// <para>It writes twice for an oversized picture — <em>Resizing…</em> and then the outcome — and a
    /// scenario that read it immediately would race a canvas. Waiting on the <b>submit control</b> rather
    /// than on the text is what makes this deterministic: <c>setBusy</c> disables it for exactly the
    /// duration of the work, which is the same mechanism that stops a person posting the original file
    /// mid-resize.</para>
    /// </summary>
    private static async Task<string> WaitForResizeReportAsync(IPage page)
    {
        // Three conditions rather than one, because each of the other two alone has a race. The status
        // element is written twice — "Resizing…" and then the outcome — so reading it on first sight
        // catches the wrong sentence; and the file control is re-enabled at the end but has not
        // necessarily been disabled yet at the moment this starts, so waiting only for enabled can
        // return before the work began. Together they are unambiguous: a settled sentence beside a
        // control that is not busy.
        await page.WaitForFunctionAsync(
            "() => { const status = document.querySelector('#picture-status');"
            + " const control = document.querySelector('#picture-file');"
            + " return status !== null && control !== null && !control.disabled"
            + " && status.textContent.length > 0 && status.textContent.indexOf('Resizing') < 0; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 60_000 });

        return await page.InnerTextAsync(StatusSelector);
    }

    /// <summary>
    /// The picture history panel's text — the surface Stage 4d added and F-106 made unreachable. Read as
    /// one string rather than row by row, because what every assertion on it wants is whether a sentence
    /// is present, and §11.4 renders the whole log untruncated.
    /// </summary>
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
