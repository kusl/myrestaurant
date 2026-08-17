using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One control on a surface, as the browser laid it out: what it is, where its box sits, and how tall
/// it is. The description is built inside the page rather than on this side of the protocol, because
/// the useful sentence — <c>a.button-secondary "Manage E2E Sixteen"</c> — is a property of the DOM at
/// that moment and nothing here could reconstruct it afterwards.
/// </summary>
/// <param name="Description">Tag, classes and accessible name, as the page described it.</param>
/// <param name="Left">The box's left edge in CSS pixels, relative to the viewport's left edge.</param>
/// <param name="Right">The box's right edge in CSS pixels, relative to the viewport's left edge.</param>
/// <param name="Height">The box's height in CSS pixels.</param>
internal sealed record MeasuredControl(string Description, double Left, double Right, double Height)
{
    /// <summary>One line for a failure message: what it was, and the numbers that made it a finding.</summary>
    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Description} [left {Left:0.#}, right {Right:0.#}, height {Height:0.#}]");
}

/// <summary>
/// What one surface looked like at one viewport width.
/// </summary>
/// <param name="Path">The path that was measured.</param>
/// <param name="ClientWidth">
/// The viewport's own width, read back from the document rather than assumed. A scenario asserts on it
/// before it asserts on anything else: every number below is relative to it, so a barrier that ran at
/// the default 1280 would pass everything and mean nothing.
/// </param>
/// <param name="ScrollWidth">
/// <c>document.documentElement.scrollWidth</c>. Greater than <paramref name="ClientWidth"/> means the
/// page scrolls sideways, which is F-59's mechanism stated as a number.
/// </param>
/// <param name="WidestOverflowHint">
/// A best-effort guess at which element is responsible, or <c>null</c>. Advisory only — see
/// <see cref="HandheldReach"/> for why this never decides anything.
/// </param>
/// <param name="Reachable">Controls whose whole box lies inside the viewport.</param>
/// <param name="OutOfReach">Controls whose box does not — F-59, restated per element.</param>
/// <param name="Undersized">Controls shorter than §11.12's touch-target minimum.</param>
internal sealed record HandheldReachReport(
    string Path,
    double ClientWidth,
    double ScrollWidth,
    string? WidestOverflowHint,
    IReadOnlyList<MeasuredControl> Reachable,
    IReadOnlyList<MeasuredControl> OutOfReach,
    IReadOnlyList<MeasuredControl> Undersized)
{
    /// <summary>Every control measured for reach, whatever its verdict.</summary>
    internal int MeasuredCount => Reachable.Count + OutOfReach.Count;

    /// <summary>Whether the document itself scrolls sideways, within a pixel of rounding.</summary>
    internal bool ScrollsSideways => ScrollWidth > ClientWidth + HandheldReach.PixelTolerance;

    /// <summary>The overflow as a sentence, for a message that has to be read once and acted on.</summary>
    internal string DescribeOverflow()
    {
        string hint = WidestOverflowHint ?? "not identified";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Path} lays out {ScrollWidth:0.#}px wide inside a {ClientWidth:0.#}px viewport"
                + $" (widest element outside a scroll container: {hint})");
    }
}

/// <summary>
/// The §11.12 reachability barrier: what a real browser says about a real page at a real handset width
/// (TECHNICAL_SPECIFICATION §11.12, §16.3 scenario 16, §16.4).
///
/// <para><b>Why this exists when <c>HandheldLayoutContractTests</c> already passes.</b> That test
/// asserts the four structural properties §11.12 states — one breakpoint, one shared vocabulary, a label
/// on every cell, the retired names gone — and every one of them is arithmetic on text. None can decide
/// whether a control is on the screen, because that is a question about layout and only a layout engine
/// answers it. F-59 was a set of pages that satisfied every text property this project was capable of
/// stating and put the only affordance on each row off the right-hand edge of a 375px screen. This is
/// the assertion that finding would have failed.</para>
///
/// <para><b>Two numbers decide, and a third only explains.</b> The document's scroll width and each
/// control's bounding box are compared against the viewport with a one-pixel tolerance. The widest
/// element is also collected and is <em>deliberately advisory</em>: a page may legitimately contain an
/// element wider than the viewport inside a scroll container of its own — <c>.page-head-areas</c> is
/// exactly that, a horizontally scrolled strip of area links whose children extend past the right edge
/// by design — so a walk that failed on those would report a finding on a correct tree. That is the
/// mistake this barrier was deferred a slice to avoid (F-41). The walk therefore skips anything inside
/// a scroller, and even then it only ever writes the sentence; the two numbers make the decision.</para>
///
/// <para><b>What is measured for reach and what only for height.</b> A control inside a scrollable strip
/// is reachable by scrolling that strip, which is what the strip is for, so the area links are measured
/// for touch-target height alone. Everything else — a row's action, a page's primary action — must lie
/// inside the viewport outright, because the only other thing to scroll is the page and scrolling the
/// page sideways is the finding.</para>
///
/// <para><b>No serializer contract.</b> The page returns one object and it is read out of a
/// <c>JsonElement</c> by hand rather than deserialised into a type. Property naming, constructor
/// selection and the accessibility of a nested record are three things that would otherwise sit between
/// a correct measurement and a green run, none of them visible in this file.</para>
/// </summary>
internal static class HandheldReach
{
    /// <summary>
    /// One CSS pixel of slack on every comparison. Sub-pixel layout, a fractional <c>clamp()</c> padding
    /// and a scrollbar-less viewport all produce differences under a pixel that mean nothing, and a
    /// barrier that failed on those is one nobody could keep green.
    /// </summary>
    internal const double PixelTolerance = 1.0;

    /// <summary>
    /// §11.12's touch-target minimum in CSS pixels: <c>--touch-target</c> is <c>2.75rem</c>, 44px at the
    /// default root size. Written here as the number rather than read back from the custom property, on
    /// purpose — the assertion is about the height a finger has to hit, and a page that redefined the
    /// variable to <c>1rem</c> would satisfy a check that asked the page what its own minimum was.
    /// </summary>
    internal const double MinimumTouchTargetPixels = 44.0;

    /// <summary>
    /// The controls that must be inside the viewport: a record row's way in, the page's own primary
    /// action, and a filter's submit. All three are the thing an operator opened the page in order to
    /// press — which is the whole membership rule, and the reason <c>.filter-actions</c> joined it in M6
    /// Slice 33 rather than being left out as "just a form". On <c>/administration/events</c> and
    /// <c>/administration/hidden-records</c> there is no record action and no page-head action at all:
    /// §11.4 makes both read-only, so the filter is the only control on the surface, and a barrier that
    /// skipped it would visit two pages and measure nothing on either (F-41).
    ///
    /// <para><c>.manage-inline-form button</c> joined in Slice 34, with the four detail surfaces the
    /// barrier now walks, and it is the same membership decision one register in: rename this table,
    /// revoke this role, reprice this item, revoke this display. Each of those is the whole reason an
    /// operator opened the page, and each was 34px tall with no font floor until that slice, because the
    /// four pages declared their own form controls inline and the copies had no touch-target height at all
    /// (F-66). The `.manage-back` link is deliberately outside the set: leaving is not the thing anybody
    /// came for.</para>
    ///
    /// <para><c>.menu-group-summary</c> and <c>.menu-group-actions a</c> joined in Slice 44, with the
    /// sections-first menu index, and they joined <b>because the surface would otherwise have gone
    /// quiet</b>. That page used to be a flat list of items, so every row's way in was a
    /// <c>.record-actions</c> link this selector already read; it is now a list of headings, and a
    /// heading's two controls — the disclosure that opens it and the link into its editor — are in
    /// neither of the three groups above. Replacing measured controls with unmeasured ones is a barrier
    /// losing coverage on a surface it still visits, which is the shape of F-70 rather than of a new
    /// feature, and the floor below cannot see it: a floor notices a group that vanished, never one that
    /// was never counted. A <c>&lt;summary&gt;</c> is admitted on the membership rule rather than as an
    /// exception to it — it occupies the position <c>.record-primary</c> holds on every other index, and
    /// it is the only control this surface introduced.</para>
    ///
    /// <para>The stream checkboxes inside the filter are deliberately <em>not</em> here. A checkbox is
    /// 1.35rem by declaration — <c>.form-field input[type="checkbox"]</c> sets <c>min-height: 0</c> on
    /// purpose — so the thing a thumb finds is the <c>.filter-choice</c> row around it, and asserting a
    /// 44px box on the input itself would report a finding on a correct tree. The row carries the
    /// touch-target height in app.css; what is untested is that it does, which is the same honest gap
    /// <c>.record-tick</c> has.</para>
    /// </summary>
    private const string ReachSelector =
        ".record-actions a, .record-actions button, .page-head-action a, .page-head-action button,"
            + " .filter-actions a, .filter-actions button, .manage-inline-form button,"
            + " .menu-group-summary, .menu-group-actions a";

    /// <summary>
    /// Measured for height only. The area links are a horizontally scrolled strip by design (§11.12,
    /// <c>.page-head-areas</c>), so their horizontal position is not a finding — but a strip of pills too
    /// short to hit is.
    /// </summary>
    private const string HeightOnlySelector = ".page-head-areas a";

    /// <summary>
    /// Present on every surface this barrier visits. Waited on rather than a record list, because a page
    /// with no records still has to lay out correctly — waiting for a list would make an empty surface
    /// hang for thirty seconds and then report the wrong thing.
    ///
    /// <para>It is also what tells an arrived detail surface from the not-found panel the same route
    /// renders, which matters as of Slice 34: four of the ten surfaces are <c>/…/{identifier}</c> routes,
    /// and a stale identifier answers 200 with a "not found" panel that has no head. So a wrong identifier
    /// fails here, naming the path, rather than passing a barrier that measured nothing.</para>
    /// </summary>
    private const string PageHeadSelector = ".page-head";

    /// <summary>
    /// The two selectors, handed to the page as one argument. A field rather than an array literal at
    /// the call site because a constant array built for every call is CA1861, which is an error under
    /// <c>ContinuousIntegrationBuild</c> — the same reason <c>RestaurantHarness</c> holds its
    /// <c>playwright install</c> arguments in a field.
    /// </summary>
    private static readonly string[] MeasurementSelectors = [ReachSelector, HeightOnlySelector];

    private static readonly TimeSpan SurfacePatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the document's own width against what it needs, measures both control sets, and — purely
    /// for the failure message — names the widest visible element that is not inside something that
    /// scrolls. Without that exclusion the answer on every administration page would be an area-link
    /// pill: correct, expected, and never the cause.
    /// </summary>
    private const string MeasurementScript = """
        (selectors) => {
            const root = document.documentElement;
            const clientWidth = root.clientWidth;
            const reachSelector = selectors[0];
            const heightOnlySelector = selectors[1];

            const describe = (element) => {
                const tag = element.tagName.toLowerCase();
                const raw = typeof element.className === 'string' ? element.className.trim() : '';
                const classes = raw.length > 0 ? '.' + raw.split(/\s+/).join('.') : '';
                const label = element.getAttribute('aria-label') || (element.textContent || '');
                const name = label.replace(/\s+/g, ' ').trim().slice(0, 60);
                return name.length > 0 ? tag + classes + ' "' + name + '"' : tag + classes;
            };

            const measure = (selector) => Array.from(document.querySelectorAll(selector)).map((element) => {
                const box = element.getBoundingClientRect();
                return {
                    description: describe(element),
                    left: box.left,
                    right: box.right,
                    height: box.height
                };
            });

            const scrolls = (element) => {
                const overflowX = getComputedStyle(element).overflowX;
                return overflowX === 'auto' || overflowX === 'scroll';
            };

            let hint = null;
            let widest = clientWidth + 1;

            for (const element of document.querySelectorAll('body *')) {
                const style = getComputedStyle(element);
                if (style.display === 'none' || style.visibility === 'hidden') {
                    continue;
                }

                let insideScroller = false;
                for (let parent = element.parentElement; parent; parent = parent.parentElement) {
                    if (scrolls(parent)) {
                        insideScroller = true;
                        break;
                    }
                }

                if (insideScroller) {
                    continue;
                }

                const box = element.getBoundingClientRect();
                if (box.width === 0 && box.height === 0) {
                    continue;
                }

                if (box.right > widest) {
                    widest = box.right;
                    hint = describe(element);
                }
            }

            return {
                clientWidth: clientWidth,
                scrollWidth: root.scrollWidth,
                widestOverflowHint: hint,
                reach: measure(reachSelector),
                heightOnly: measure(heightOnlySelector)
            };
        }
        """;

    /// <summary>
    /// Navigates to <paramref name="path"/> and measures it.
    ///
    /// <para>Everything is read in one <c>EvaluateAsync</c> round trip rather than through
    /// <c>BoundingBoxAsync</c> per element. A page with a dozen rows would otherwise be a dozen protocol
    /// round trips interleaved with layout, and the numbers would not all describe the same moment —
    /// which is the difference between measuring a page and measuring several.</para>
    /// </summary>
    internal static async Task<HandheldReachReport> MeasureAsync(IPage page, string path)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(path);

        try
        {
            await page.Locator(PageHeadSelector).First.WaitForAsync(
                new LocatorWaitForOptions { Timeout = (float)SurfacePatience.TotalMilliseconds });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"'{path}' never rendered a page head, so there was nothing to measure. Either the"
                    + " surface did not load, or it is not one of §11.4's administration indexes.",
                exception);
        }

        JsonElement? evaluated = await page.EvaluateAsync(MeasurementScript, MeasurementSelectors);

        if (evaluated is not { } measurement)
        {
            throw new InvalidOperationException(
                $"Measuring '{path}' returned nothing. The page evaluated the script and produced"
                    + " undefined, which it cannot do unless the script itself was replaced.");
        }

        double clientWidth = measurement.GetProperty("clientWidth").GetDouble();
        double scrollWidth = measurement.GetProperty("scrollWidth").GetDouble();

        JsonElement hint = measurement.GetProperty("widestOverflowHint");
        string? widestOverflowHint = hint.ValueKind == JsonValueKind.String ? hint.GetString() : null;

        List<MeasuredControl> reachable = [];
        List<MeasuredControl> outOfReach = [];
        List<MeasuredControl> undersized = [];

        foreach (MeasuredControl control in ReadControls(measurement.GetProperty("reach")))
        {
            bool inside = control.Left >= -PixelTolerance
                && control.Right <= clientWidth + PixelTolerance;

            (inside ? reachable : outOfReach).Add(control);

            if (control.Height < MinimumTouchTargetPixels - PixelTolerance)
            {
                undersized.Add(control);
            }
        }

        foreach (MeasuredControl control in ReadControls(measurement.GetProperty("heightOnly")))
        {
            if (control.Height < MinimumTouchTargetPixels - PixelTolerance)
            {
                undersized.Add(control);
            }
        }

        return new HandheldReachReport(
            path,
            clientWidth,
            scrollWidth,
            widestOverflowHint,
            reachable,
            outOfReach,
            undersized);
    }

    /// <summary>
    /// Every line in a set of measurements, formatted for a failure message. Empty reads as
    /// <c>(none)</c> rather than as an empty string, because a message with a blank where a list should
    /// be is one a reader has to go and check.
    /// </summary>
    internal static string Format(IEnumerable<MeasuredControl> controls)
    {
        string joined = string.Join("; ", controls.Select(control => control.Describe()));
        return joined.Length == 0 ? "(none)" : joined;
    }

    private static List<MeasuredControl> ReadControls(JsonElement array)
    {
        List<MeasuredControl> controls = [];

        foreach (JsonElement element in array.EnumerateArray())
        {
            controls.Add(new MeasuredControl(
                element.GetProperty("description").GetString() ?? "(unnamed element)",
                element.GetProperty("left").GetDouble(),
                element.GetProperty("right").GetDouble(),
                element.GetProperty("height").GetDouble()));
        }

        return controls;
    }
}
