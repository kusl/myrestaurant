using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// Putting a photograph on a dish through §11.4's own form, and knowing when a browser has finished
/// decoding one (TECHNICAL_SPECIFICATION §7, §8.2, §11.4, §16.3; Stage 1e of
/// <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
///
/// <para><b>Why this file exists at all, and it is the blocker the plan named.</b>
/// <c>docs/MENU_AND_HANDHELD_PLAN.md</c> carried one line for five slices — <em>"nothing measures §11.1
/// with a picture on a card … attaching a photograph inside scenario 21 means extracting the upload
/// journey into the harness"</em> — and that sentence was right about the mechanism. The upload existed
/// only as steps inlined in <c>MenuPictureScenarios</c>, with the selectors as <c>private const</c>, and
/// a private member cannot be reached from a second scenario class. So the choice was to move it or to
/// paste it, and pasting a journey is F-59's mechanism with F-100's ruling already written down against
/// it — the same choice <see cref="TableJourneys.SeatGuestAsync"/> faced one slice ago and resolved the
/// same way.
/// </para>
///
/// <para><b>The selectors live here and nowhere else.</b> That is this directory's standing rule rather
/// than a preference, and <c>AdministrationJourneys</c> states it in as many words about §7's
/// hidden-heading suffix: <em>"declared once here because two methods above strip it, and a second
/// spelling of it would make one of them silently stop matching"</em>. An <c>id</c> renamed on the form
/// is not a compile error and not an exception; it is a locator that waits a minute and then reports the
/// wrong thing, once per file that spelled it. The two scripts below therefore compose their selectors
/// from these constants rather than quoting them, because a script is a place a second spelling hides
/// particularly well.</para>
///
/// <para><b>What is deliberately NOT here: the three upload scenarios' own steps.</b> Scenarios 18, 19
/// and 20 assert <em>between</em> the actions this journey performs as one — the status line before the
/// submit, the file the control holds after the downscaler ran, the flash on the redirected GET, the
/// stored facts on the panel. Folding them onto this method would mean either growing assertion hooks
/// into a journey, which this directory forbids because only claims about the product belong in a
/// scenario, or those scenarios losing the intermediate claims that are the whole reason they exist —
/// and F-106 was found in exactly one of those gaps. They keep their steps and give up only the
/// strings.</para>
///
/// <para><b>The journey ends when the picture has DECODED, not when the row committed.</b> That is the
/// difference between an arrangement and a hope — see <see cref="WaitForDecodedAsync"/> for the hole it
/// exists because of.</para>
/// </summary>
internal static class MenuPictureJourneys
{
    /// <summary>
    /// §11.4's file control. The <c>id</c> the page writes, not the field name the handler looks a part
    /// up by — those are two different strings and only this one is a selector.
    /// </summary>
    internal const string FileInput = "#picture-file";

    /// <summary>
    /// The line <c>wwwroot/js/menu-picture.js</c> writes, which the form resolves through
    /// <c>aria-describedby</c>. Hidden until the script has something to say, which is what makes
    /// waiting for it <em>visible</em> a real signal that the browser side ran at all.
    /// </summary>
    internal const string Status = "#picture-status";

    /// <summary>The flash on the redirected GET — the request F-106 made answer 500.</summary>
    internal const string Flash = ".status-success";

    /// <summary>§11.4's own thumbnail of what is now stored.</summary>
    internal const string Thumbnail = "img.manage-picture-image";

    /// <summary>The caption editor's field, which writes <c>alt_text</c> (Stage 4d).</summary>
    internal const string AltTextInput = "#picture-alt-text";

    /// <summary>§11.4's account of what it holds: the stored media type, the stored size, and when.</summary>
    internal const string Facts = "figcaption.manage-picture-facts";

    /// <summary>
    /// How long a picture is given to travel through a file control, a canvas, a multipart POST, a
    /// redirect and a decode. The generous figure every §16.3 wait uses, and for the same reason: a
    /// timeout during the arrangement reports a defect in the thing the scenario was about to test.
    /// </summary>
    private const float UploadPatienceMilliseconds = 60_000;

    /// <summary>
    /// Attaches <paramref name="picture"/> to <paramref name="item"/> through
    /// <c>/administration/menu/{id}</c>, and returns once §11.4's panel is showing a picture that has
    /// decoded in the browser.
    ///
    /// <para><b>Through the real form, and there is no other way in.</b> §7 stores what it was given and
    /// the route hands the stored column straight back out as a response header, so an arrangement that
    /// reached past the surface — an INSERT, a hand-built POST — would be arranging a state the
    /// application cannot itself produce. §16.3 refuses that in general; here it would also skip the
    /// browser-side downscaler, which is the component that decides what bytes actually arrive.</para>
    ///
    /// <para><b>The browser side is waited for before the submit is pressed</b>, because
    /// <c>menu-picture.js</c> may replace the chosen file with a smaller one and a form posted during
    /// that work posts the original — which the server refuses, correctly, with a message about size the
    /// operator has just watched the page promise to handle.</para>
    ///
    /// <para><paramref name="caption"/> is optional and writes <c>alt_text</c> through the second form
    /// (Stage 4d). Passing <c>null</c> leaves it unwritten, which stores <c>""</c> — and <c>""</c> is the
    /// <b>right</b> answer for most pictures on a menu rather than a missing value, because the card
    /// around the thumbnail already carries the dish's name and its price as text (F-103).</para>
    /// </summary>
    /// <param name="administrator">A page signed in as an administrator.</param>
    /// <param name="item">The dish, as <see cref="AdministrationJourneys.CreateMenuItemAsync"/> returned it.</param>
    /// <param name="picture">The bytes — see <see cref="PictureFixtures"/>.</param>
    /// <param name="fileName">What the file control is told the file is called.</param>
    /// <param name="mimeType">What the file control is told the file is. A claim, never a fact (F-109).</param>
    /// <param name="caption">The caption to write, or <c>null</c> to leave <c>alt_text</c> empty.</param>
    /// <returns>The stored picture's decoded width in pixels, as §11.4's own thumbnail reports it.</returns>
    internal static async Task<int> AttachAsync(
        IPage administrator,
        MenuItemOnTheMenu item,
        byte[] picture,
        string fileName = "dish.png",
        string mimeType = "image/png",
        string? caption = null)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(picture);

        await administrator.GotoAsync($"/administration/menu/{item.Identifier:D}");

        await administrator.SetInputFilesAsync(
            FileInput,
            new FilePayload
            {
                Name = fileName,
                MimeType = mimeType,
                Buffer = picture,
            });

        await SettleAsync(administrator);

        await administrator.ClickAsync("button:has-text('Attach picture')");

        // The redirected GET. Waited on through the flash rather than through a navigation, because a
        // 500 also completes a navigation — which is precisely how F-106 stayed invisible.
        try
        {
            await administrator.Locator(Flash).WaitForAsync(
                new LocatorWaitForOptions { Timeout = UploadPatienceMilliseconds });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Attaching a picture to '{item.Name}' never reached a success flash. Either the POST"
                    + " was refused, or the page the redirect landed on threw — which is F-106's exact"
                    + " shape, and is why this waits on the flash rather than on the navigation.",
                exception);
        }

        if (caption is not null)
        {
            await administrator.FillAsync(AltTextInput, caption);
            await administrator.ClickAsync("button:has-text('Save caption')");

            await administrator.Locator(Flash).WaitForAsync(
                new LocatorWaitForOptions { Timeout = UploadPatienceMilliseconds });
        }

        // Decoded rather than merely present, on this surface first, so that a scenario reading the
        // GUEST's copy is reading back something this journey has already proved the server can serve.
        return await WaitForDecodedAsync(administrator, Thumbnail);
    }

    /// <summary>
    /// Waits until every element matching <paramref name="selector"/> has a non-zero
    /// <c>naturalWidth</c>, and returns the first one's.
    ///
    /// <para><b>This is a gate rather than a courtesy, and Stage 1e is the slice that found out why.</b>
    /// An <c>&lt;img&gt;</c> that has not decoded has <b>no intrinsic size</b>. §11.1's detail-panel
    /// picture declares <c>max-width: 100%</c> and <c>height: auto</c> and no width or height of its own,
    /// so before the bytes arrive its box is <c>0×0</c> — and a <c>0×0</c> box lies inside every viewport
    /// there is. A barrier measuring it would report the element reachable, having measured a
    /// placeholder.</para>
    ///
    /// <para><b>And a census cannot see that</b>, which is what lifts it from a race to a hole.
    /// <see cref="HandheldReach"/> refuses a run in which a required selector matched nothing; an
    /// undecoded image <em>matches</em>, so it appears in the census as a one and every verdict computed
    /// from it is true of an element that is not there yet. That is why
    /// <see cref="HandheldSurface.ReachOnlySelectors"/> carries a collapsed-box refusal of its own
    /// <em>as well</em> as this wait: the wait makes the arrangement deterministic, and the refusal is
    /// what says so if a later slice removes the wait.</para>
    ///
    /// <para><b>Every match rather than the first, deliberately.</b> §11.1 renders the same picture twice
    /// at once — cropped on the card, uncropped in the panel — and a wait satisfied by either one alone
    /// is a wait that sometimes returns before the element the scenario is about has any pixels.</para>
    ///
    /// <para>The two failure modes it collapses are the same fact about the screen right now:
    /// <c>naturalWidth</c> is zero for an image still loading and zero for one that failed, and neither
    /// is a picture.</para>
    /// </summary>
    internal static async Task<int> WaitForDecodedAsync(IPage page, string selector)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(selector);

        ILocator images = page.Locator(selector);

        await images.First.WaitForAsync(
            new LocatorWaitForOptions { Timeout = UploadPatienceMilliseconds });

        try
        {
            await page.WaitForFunctionAsync(
                DecodedScript,
                selector,
                new PageWaitForFunctionOptions { Timeout = UploadPatienceMilliseconds });
        }
        catch (PlaywrightException exception)
        {
            int found = await images.CountAsync();

            throw new InvalidOperationException(
                $"'{selector}' matched {found} element(s) and at least one never decoded. The markup"
                    + " carries a src and the browser could not turn it into pixels — §7's route"
                    + " answered something other than the stored bytes, the stored content type"
                    + " disagrees with them, or §11.11's img-src refused the origin. An undecoded"
                    + " <img> still has a box, so anything measuring this element would have measured a"
                    + " placeholder and reported it fine.",
                exception);
        }

        return await images.First.EvaluateAsync<int>("element => element.naturalWidth");
    }

    /// <summary>
    /// Every match has pixels. The selector arrives as an argument rather than being written into the
    /// script, so this one function serves §11.4's thumbnail and both of §11.1's pictures.
    /// </summary>
    private const string DecodedScript = """
        (selector) => {
            const images = Array.from(document.querySelectorAll(selector));
            return images.length > 0 && images.every((image) => image.naturalWidth > 0);
        }
        """;

    /// <summary>
    /// Returns once the browser side has stopped working on the chosen file.
    ///
    /// <para>Three conditions rather than one, because each alone has a race, and the reasoning is the
    /// one <c>MenuPictureScenarios</c> already carries: the status element is written twice for an
    /// oversized picture (<em>Resizing…</em>, then the outcome), so reading it on first sight catches the
    /// wrong sentence; and the control is re-enabled at the end but has not necessarily been disabled yet
    /// when the wait starts, so waiting only for enabled can return before the work began. Together they
    /// are unambiguous: a settled sentence beside a control that is not busy.</para>
    ///
    /// <para>A file already inside §8.2's cap is left completely alone and the script says so in a single
    /// write, so this returns almost immediately for the common case. It is not a fixed delay.</para>
    ///
    /// <para><b>Internal rather than private, because scenario 19 needs exactly this wait and used to
    /// carry its own copy of the script.</b> That scenario asserts on the sentence the status line
    /// settles at, which <see cref="AttachAsync"/> does not expose and should not — but the moment the
    /// browser stops working is the same moment for both, and two copies of a five-line predicate about
    /// two selectors is two places a rename has to reach.</para>
    /// </summary>
    internal static Task SettleAsync(IPage page)
        => page.WaitForFunctionAsync(
            SettledScript,
            null,
            new PageWaitForFunctionOptions { Timeout = UploadPatienceMilliseconds });

    /// <summary>
    /// Composed from <see cref="Status"/> and <see cref="FileInput"/> rather than quoting them, so this
    /// file keeps one spelling of each selector. <c>static readonly</c> rather than <c>const</c> because
    /// the value is assembled at type initialisation and nothing here needs a compile-time constant —
    /// and the word <em>Resizing</em> is the script's own sentence rather than a selector, so it is
    /// written where it is read.
    /// </summary>
    private static readonly string SettledScript =
        "() => { const status = document.querySelector('" + Status + "');"
        + " const control = document.querySelector('" + FileInput + "');"
        + " return status !== null && control !== null && !control.disabled"
        + " && status.textContent.length > 0 && status.textContent.indexOf('Resizing') < 0; }";
}
