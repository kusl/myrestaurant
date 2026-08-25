using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One CSS selector this barrier measures through, and whether the surface it belongs to is required
/// to render something it matches.
///
/// <para><b><c>MustMatch</c> is the residual scenario 16 named, made decidable.</b> That scenario's own
/// comment records it exactly: <em>"a floor cannot notice a group that grew, only one that vanished …
/// making it a census honestly would mean attributing each measured control to the selector that
/// matched it, which <c>HandheldReachReport</c> does not carry — a real gate, deliberately not built in
/// the slice that found the defect"</em>. It carries it now. A total floor is satisfied by one selector
/// matching twice as often as it should while another matches nothing, and this repository has already
/// had the second half of that for fifteen slices: <c>.record-actions button</c> was in the barrier from
/// the day it was written and matched <b>nothing</b> until Slice 48, with nothing able to say so.</para>
///
/// <para><b>Why it is per selector rather than on by default.</b> Turning it on for
/// <see cref="HandheldSurface.Administration"/> is a claim about ten surfaces and the rows one scenario
/// happens to arrange, and this slice cannot check that claim against a browser — which is precisely
/// F-116's mistake, a gate shipped on its author's belief about a tree. So the administration set
/// declares every selector optional and the guest set declares every selector required, because the
/// guest set is the one this slice chose and is responsible for. Widening it is a decision a later slice
/// makes after a green run, and the census is in the failure message either way.</para>
/// </summary>
/// <param name="Css">The selector, as the page will be asked for it.</param>
/// <param name="MustMatch">
/// Whether the surface must render at least one element this selector matches. A selector that matches
/// nothing is indistinguishable from one matching everything it should, which is the whole finding.
/// </param>
internal sealed record HandheldSelector(string Css, bool MustMatch)
{
    /// <summary>A selector whose absence from a surface is not a finding.</summary>
    internal static HandheldSelector Optional(string css) => new(css, MustMatch: false);

    /// <summary>A selector the surface is required to render something for.</summary>
    internal static HandheldSelector Required(string css) => new(css, MustMatch: true);
}

/// <summary>
/// One control on a surface, as the browser laid it out: what it is, which selector found it, where its
/// box sits, how tall it is, and what size its text computed to. The description is built inside the
/// page rather than on this side of the protocol, because the useful sentence —
/// <c>a.button-secondary "Manage E2E Sixteen"</c> — is a property of the DOM at that moment and nothing
/// here could reconstruct it afterwards.
/// </summary>
/// <param name="Selector">The selector that matched this element, so a census can be attributed.</param>
/// <param name="Description">Tag, classes and accessible name, as the page described it.</param>
/// <param name="Left">The box's left edge in CSS pixels, relative to the viewport's left edge.</param>
/// <param name="Right">The box's right edge in CSS pixels, relative to the viewport's left edge.</param>
/// <param name="Height">The box's height in CSS pixels.</param>
/// <param name="FontSizePixels">
/// <c>getComputedStyle(element).fontSize</c> in CSS pixels. Read for every measured element and
/// asserted only for the font-floor set — see <see cref="HandheldSurface.FontFloorSelectors"/>.
/// </param>
internal sealed record MeasuredControl(
    string Selector,
    string Description,
    double Left,
    double Right,
    double Height,
    double FontSizePixels)
{
    /// <summary>One line for a failure message: what it was, and the numbers that made it a finding.</summary>
    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Description} [left {Left:0.#}, right {Right:0.#}, height {Height:0.#},"
            + $" font {FontSizePixels:0.#}px, via `{Selector}`]");
}

/// <summary>
/// What this barrier measures on one kind of page, and what it anchors on to know the page arrived.
///
/// <para><b>Why this is a value rather than a set of constants (M6 Slice 64).</b> The barrier was
/// written for §11.4 and named for §11.12, which is normative for <em>every surface, every screen</em>.
/// The gap was never hidden — §11.12's own closing paragraph says the browser-level assertion "walks
/// §11.4's surfaces" — but the only place the <em>scope</em> was written down in the code was the
/// exception message, which told a reader that their page "is not one of §11.4's administration
/// indexes". A guest surface arriving at that sentence reads as a broken page rather than as an
/// out-of-scope one. The selectors, the anchor and the membership argument are now a value per surface,
/// and the class is what it was named for.</para>
/// </summary>
/// <param name="Name">What this surface is called in a failure message.</param>
/// <param name="AnchorSelector">
/// Present on every page of this kind, and waited on before anything is measured. Chosen so that an
/// arrived page is told from a page that answered 200 with something else on it.
/// </param>
/// <param name="ReachSelectors">
/// Controls whose whole box must lie inside the viewport <em>and</em> which must clear §11.12's
/// touch-target floor.
/// </param>
/// <param name="HeightOnlySelectors">
/// Controls measured for height alone, because their horizontal position is legitimately outside the
/// viewport — anything inside a scroll container of its own.
/// </param>
/// <param name="FontFloorSelectors">
/// Text controls that must clear §11.12's 16px font floor. Separate from the two above because it is a
/// different rule with a different failure: an under-sized field is perfectly placed, perfectly tall,
/// and zooms the whole viewport on focus in iOS Safari without zooming back.
/// </param>
internal sealed record HandheldSurface(
    string Name,
    string AnchorSelector,
    IReadOnlyList<HandheldSelector> ReachSelectors,
    IReadOnlyList<HandheldSelector> HeightOnlySelectors,
    IReadOnlyList<HandheldSelector> FontFloorSelectors)
{
    /// <summary>
    /// §11.4's administration surfaces — six indexes and four detail pages (§16.3 scenario 16).
    ///
    /// <para><b>The membership rule is the thing an operator opened the page in order to press.</b> A
    /// record row's way in, the page's own primary action, and a filter's submit. That is why
    /// <c>.filter-actions</c> joined in M6 Slice 33 rather than being left out as "just a form": on
    /// <c>/administration/events</c> and <c>/administration/hidden-records</c> there is no record action
    /// and no page-head action at all, §11.4 makes both read-only, so the filter is the only control on
    /// the surface and a barrier that skipped it would visit two pages and measure nothing on either
    /// (F-41).</para>
    ///
    /// <para><c>.manage-inline-form button</c> joined in Slice 34 with the four detail surfaces, and it
    /// is the same membership decision one register in: rename this table, revoke this role, reprice
    /// this item, revoke this display. Each was 34px tall with no font floor until that slice, because
    /// the four pages declared their own form controls inline and the copies had no touch-target height
    /// at all (F-66). The <c>.manage-back</c> link is deliberately outside the set: leaving is not the
    /// thing anybody came for.</para>
    ///
    /// <para><c>.menu-group-summary</c> and <c>.menu-group-actions a</c> joined in Slice 44 with the
    /// sections-first menu index, and they joined <b>because the surface would otherwise have gone
    /// quiet</b>: that page used to be a flat list whose every row's way in was a <c>.record-actions</c>
    /// link, and it is now a list of headings whose two controls are in neither group. Replacing
    /// measured controls with unmeasured ones is a barrier losing coverage on a surface it still visits,
    /// and a floor cannot see it. <c>.menu-group-actions button</c> joined in Slice 47 with the
    /// resequencing verb, on F-93's rule obeyed rather than rediscovered: a surface acquiring a new
    /// <em>kind</em> of control acquires a selector in the same slice.</para>
    ///
    /// <para><b>Every selector here is <see cref="HandheldSelector.Optional"/>, and that is a recorded
    /// decision rather than a default.</b> <c>.record-actions button</c> matched nothing for fifteen
    /// slices; whether the other nine match on the arrangement scenario 16 happens to build is a claim
    /// about ten pages that this slice cannot check in a browser. Requiring them here on an argument
    /// rather than on a run is the shape of F-116 exactly. The census is reported in every failure
    /// message this type produces, so the next slice can turn them on from evidence.</para>
    ///
    /// <para>The stream checkboxes inside the filter are deliberately <em>not</em> here. A checkbox is
    /// 1.35rem by declaration — <c>.form-field input[type="checkbox"]</c> sets <c>min-height: 0</c> on
    /// purpose — so the thing a thumb finds is the <c>.filter-choice</c> row around it, and asserting a
    /// 44px box on the input itself would report a finding on a correct tree. The row carries the
    /// touch-target height in <c>app.css</c>; what is untested is that it does, which is the same honest
    /// gap <c>.record-tick</c> has.</para>
    /// </summary>
    internal static HandheldSurface Administration { get; } = new(
        "§11.4's administration surfaces",
        ".page-head",
        [
            HandheldSelector.Optional(".record-actions a"),
            HandheldSelector.Optional(".record-actions button"),
            HandheldSelector.Optional(".page-head-action a"),
            HandheldSelector.Optional(".page-head-action button"),
            HandheldSelector.Optional(".filter-actions a"),
            HandheldSelector.Optional(".filter-actions button"),
            HandheldSelector.Optional(".manage-inline-form button"),
            HandheldSelector.Optional(".menu-group-summary"),
            HandheldSelector.Optional(".menu-group-actions a"),
            HandheldSelector.Optional(".menu-group-actions button"),
        ],
        [
            // The area links are a horizontally scrolled strip by design (§11.12,
            // `.page-head-areas`), so their horizontal position is not a finding — but a strip of
            // pills too short to hit is.
            HandheldSelector.Optional(".page-head-areas a"),
        ],
        // §11.12's font floor is not asserted here, and the reason is the one above: it would be a
        // claim about every text control on ten pages, made from reading a stylesheet. The guest
        // surface below turns it on for the three controls this slice can account for.
        []);

    /// <summary>
    /// §11.1's guest ordering surface — the menu, the detail panel, the basket and Send (§16.3 scenario
    /// 21, Stage 1d of <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
    ///
    /// <para><b>This is the surface §11.12 was written for and the last one to be measured.</b> R§1 says
    /// guests order from their own phones; every one of Stage 1's slices measured the surfaces
    /// <em>staff</em> use, because F-59 was found there. The guest's menu meanwhile acquired headings, a
    /// detail panel, a photograph, a like and — in Slice 60 — a second control beside a refused card,
    /// and its box model was never once laid out at 375px by anything that would report on it.</para>
    ///
    /// <para><b>The membership rule is the same one, read for a guest instead of an operator:</b> the
    /// thing they opened the page in order to press. A dish's card; the way into a refused dish's panel;
    /// the like; Add to basket; a staged line's Take out; Send. The two quantity boxes are here as well
    /// and they are not controls in the same sense — they are in because <b>F-118 is one of them</b>,
    /// and a rule proved on the surface that broke it is worth more than a rule proved elsewhere.</para>
    ///
    /// <para><b>Everything here is <see cref="HandheldSelector.Required"/>.</b> Scenario 21 arranges all
    /// of it: two dishes on the menu, one 86'd by the kitchen so its card is refused and its way-in
    /// control renders, a panel open on the refused dish, and one staged line so the basket has controls
    /// and Send has something to send. A selector here matching nothing means either the arrangement
    /// changed or a class was renamed, and both are things this barrier should say out loud rather than
    /// pass over — which is exactly what the administration set could not do about
    /// <c>.record-actions button</c> for fifteen slices.</para>
    ///
    /// <para><b>Nothing is measured for height alone</b>, because nothing on this surface is inside a
    /// scroll container of its own. That is stated rather than left as an empty list: the guest area has
    /// no <c>.page-head-areas</c> strip, and if one ever arrives it belongs in that group.</para>
    ///
    /// <para><b>Every selector is scoped to the island</b> (<c>#table-order-surface</c>) for the reason
    /// every selector in <see cref="TableOrderJourneys"/> is: the page around it is static SSR and
    /// carries controls of its own, and a barrier that measured the layout's sign-out link would be
    /// asserting something about a surface it is not visiting.</para>
    /// </summary>
    internal static HandheldSurface GuestOrder { get; } = new(
        "§11.1's guest ordering surface",
        // The island as rendered by a live circuit that has finished loading — §11.10's pair, both
        // halves demanded. `[data-live='true']` alone matches the circuit's FIRST render, where the
        // island is the single line "Loading your table…" and every selector below matches nothing;
        // a barrier anchored on that would measure an empty page and report ten absences.
        "#table-order-surface[data-live='true'][data-loaded='true']",
        [
            HandheldSelector.Required("#table-order-surface button.order-menu-choice"),
            HandheldSelector.Required("#table-order-surface button.order-menu-inspect"),
            HandheldSelector.Required("#table-order-surface button.order-menu-like"),
            HandheldSelector.Required("#table-order-surface .order-picker .form-actions button"),
            HandheldSelector.Required("#table-order-surface .order-basket-controls button"),
            HandheldSelector.Required("#table-order-surface .order-send button"),
            HandheldSelector.Required("#table-order-surface .order-picker-quantity input"),
            HandheldSelector.Required("#table-order-surface .order-basket-quantity input"),
        ],
        [],
        [
            // §11.12's 16px floor, asserted against a RENDERED element for the first time anywhere in
            // this repository (F-118). `HandheldLayoutContractTests` asserts that the declaration
            // exists in `app.css`; it cannot know which elements a page renders, and the control this
            // found renders outside every arrangement that carries the declaration. The two types are
            // named rather than a bare `input` because a checkbox is deliberately exempt —
            // `.order-line-remove input[type="checkbox"]` is 1.35rem by declaration and the thumb
            // target is the label around it.
            HandheldSelector.Required("#table-order-surface input[type=\"text\"]"),
            HandheldSelector.Required("#table-order-surface input[type=\"number\"]"),
        ]);
}

/// <summary>
/// What one surface looked like at one viewport width.
/// </summary>
/// <param name="Surface">Which surface's rules were applied.</param>
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
/// <param name="UndersizedText">Text controls under §11.12's 16px font floor (F-118).</param>
/// <param name="Census">
/// How many elements each declared selector matched. This is the record scenario 16's comment said
/// <c>HandheldReachReport</c> did not carry.
/// </param>
internal sealed record HandheldReachReport(
    HandheldSurface Surface,
    string Path,
    double ClientWidth,
    double ScrollWidth,
    string? WidestOverflowHint,
    IReadOnlyList<MeasuredControl> Reachable,
    IReadOnlyList<MeasuredControl> OutOfReach,
    IReadOnlyList<MeasuredControl> Undersized,
    IReadOnlyList<MeasuredControl> UndersizedText,
    IReadOnlyDictionary<string, int> Census)
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

    /// <summary>
    /// The per-selector census as one line. Written into every failure message this barrier produces,
    /// because the question a reader has when a floor fails — <em>which group went quiet?</em> — is one
    /// a total can never answer.
    /// </summary>
    internal string DescribeCensus()
        => string.Join(
            ", ",
            Census.Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"`{entry.Key}` × {entry.Value}")));
}

/// <summary>
/// The §11.12 reachability barrier: what a real browser says about a real page at a real handset width
/// (TECHNICAL_SPECIFICATION §11.12, §16.3 scenarios 16 and 21, §16.4).
///
/// <para><b>Why this exists when <c>HandheldLayoutContractTests</c> already passes.</b> That test
/// asserts the structural properties §11.12 states — one breakpoint, one shared vocabulary, a label on
/// every cell, the retired names gone — and every one of them is arithmetic on text. None can decide
/// whether a control is on the screen, because that is a question about layout and only a layout engine
/// answers it. F-59 was a set of pages that satisfied every text property this project was capable of
/// stating and put the only affordance on each row off the right-hand edge of a 375px screen. This is
/// the assertion that finding would have failed.</para>
///
/// <para><b>Nor can a text gate decide whether a rule reaches an element.</b> That is the second thing
/// this barrier is for and it took until Slice 64 to be used for it. <c>app.css</c> declares §11.12's
/// control rule against <c>.form-field input</c>; whether the page put its <c>&lt;input&gt;</c> inside a
/// <c>.form-field</c> is a fact about the markup that no reading of the stylesheet can produce, and
/// §11.1's basket had not (F-118). A computed style read off a rendered element is the only instrument
/// in this repository that can see it.</para>
///
/// <para><b>Three numbers decide, and a fourth only explains.</b> The document's scroll width, each
/// control's bounding box and each text control's computed font size are compared against the viewport
/// and against §11.12's two floors, with a one-pixel tolerance. The widest element is also collected and
/// is <em>deliberately advisory</em>: a page may legitimately contain an element wider than the viewport
/// inside a scroll container of its own — <c>.page-head-areas</c> is exactly that, a horizontally
/// scrolled strip of area links whose children extend past the right edge by design — so a walk that
/// failed on those would report a finding on a correct tree. That is the mistake this barrier was
/// deferred a slice to avoid (F-41). The walk therefore skips anything inside a scroller, and even then
/// it only ever writes the sentence; the numbers make the decision.</para>
///
/// <para><b>What is measured for reach and what only for height.</b> A control inside a scrollable strip
/// is reachable by scrolling that strip, which is what the strip is for, so the area links are measured
/// for touch-target height alone. Everything else — a row's action, a page's primary action, a dish's
/// card — must lie inside the viewport outright, because the only other thing to scroll is the page and
/// scrolling the page sideways is the finding.</para>
///
/// <para><b>Navigation and measurement are two methods (Slice 64).</b> <see cref="MeasureAsync"/> is a
/// static-SSR page: go there, wait for the anchor, measure. <see cref="MeasureHereAsync"/> measures the
/// page as it stands, and §11.1 needs it — the guest surface is an interactive island whose measurable
/// state is <em>arranged by pressing things</em>, so a navigation would destroy the chosen dish, the
/// open panel and the staged basket line in order to look at them.</para>
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
    /// §11.12's font floor in CSS pixels, and it is a platform constant rather than a taste. Under 16px
    /// iOS Safari zooms the whole viewport when the control takes focus and does not zoom back out, so
    /// one under-sized field breaks the layout of the page around it on the platform R§1 says most
    /// guests are holding. Written as the number for <see cref="MinimumTouchTargetPixels"/>' reason: a
    /// page that redefined its own root size would satisfy a check that asked the page.
    ///
    /// <para><b><see cref="PixelTolerance"/> is deliberately NOT applied to this one, and the reason is
    /// that the two comparisons are about different kinds of number.</b> A bounding box is the output of
    /// a layout engine and carries sub-pixel rounding, a fractional <c>clamp()</c> padding and a
    /// scrollbar's width, so a barrier failing on a difference under a pixel is one nobody could keep
    /// green. A computed font size is none of those things: <c>max(1rem, 1em)</c> against a 16px root
    /// computes to exactly 16, and every value under it was written under it — <c>0.95rem</c> is 15.2px
    /// and is a decision somebody made. Giving this a pixel of slack would pass a 15px control, which is
    /// a control iOS zooms. <b>The sensitivity proof is what caught that</b>: the transcription of this
    /// method reported green on a planted 15px field while every other planted defect reported, which is
    /// a gate that would have shipped believing itself sensitive.</para>
    /// </summary>
    internal const double MinimumTextFontPixels = 16.0;

    private static readonly TimeSpan SurfacePatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Navigates to <paramref name="path"/> and measures it as one of §11.4's administration surfaces.
    ///
    /// <para>The signature is what it was before Slice 64 on purpose: §16.3 scenario 16 calls this ten
    /// times and none of those call sites moved, because a surface argument threaded through a caller
    /// that only ever passes one value is an argument that exists to be got wrong.</para>
    /// </summary>
    internal static Task<HandheldReachReport> MeasureAsync(IPage page, string path)
        => MeasureAsync(page, path, HandheldSurface.Administration);

    /// <summary>
    /// Navigates to <paramref name="path"/>, waits for <paramref name="surface"/>'s anchor, and measures
    /// it.
    /// </summary>
    internal static async Task<HandheldReachReport> MeasureAsync(
        IPage page,
        string path,
        HandheldSurface surface)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(surface);

        await page.GotoAsync(path);

        try
        {
            await page.Locator(surface.AnchorSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)SurfacePatience.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{path}' never rendered `{surface.AnchorSelector}`, so there was nothing to"
                    + $" measure. Either the surface did not load, or it is not one of"
                    + $" {surface.Name}."),
                exception);
        }

        return await MeasureHereAsync(page, path, surface);
    }

    /// <summary>
    /// Measures the page <b>as it currently stands</b>, without navigating and without waiting for an
    /// anchor.
    ///
    /// <para>This is the entry point for an interactive surface. §11.1's picker, detail panel and basket
    /// are circuit state: a chosen dish, an open panel and a staged line exist because a scenario pressed
    /// things, and <c>GotoAsync</c> would tear the circuit down and rebuild it with none of that on
    /// screen. The caller is therefore responsible for the surface being live and arranged, which for
    /// §11.1 means <see cref="TableOrderJourneys.WaitForLiveSurfaceAsync"/> has already returned.</para>
    ///
    /// <para>Everything is read in one <c>EvaluateAsync</c> round trip rather than through
    /// <c>BoundingBoxAsync</c> per element. A page with a dozen rows would otherwise be a dozen protocol
    /// round trips interleaved with layout, and the numbers would not all describe the same moment —
    /// which is the difference between measuring a page and measuring several.</para>
    ///
    /// <para><b>It throws on a selector that matched nothing when the surface said it must match</b>,
    /// rather than returning a report a caller then has to remember to check. That is this directory's
    /// standing convention — a journey reports an arrangement failure as an
    /// <see cref="InvalidOperationException"/> naming what the surface was showing, and only assertions
    /// about the product live in a scenario. A group that went quiet is an arrangement failure: nothing
    /// about the product is wrong, and every verdict computed from it is true of a smaller page than the
    /// one the scenario meant to measure.</para>
    /// </summary>
    internal static async Task<HandheldReachReport> MeasureHereAsync(
        IPage page,
        string path,
        HandheldSurface surface)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(surface);

        string[][] selectors =
        [
            [.. surface.ReachSelectors.Select(selector => selector.Css)],
            [.. surface.HeightOnlySelectors.Select(selector => selector.Css)],
            [.. surface.FontFloorSelectors.Select(selector => selector.Css)],
        ];

        JsonElement? evaluated = await page.EvaluateAsync(MeasurementScript, selectors);

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

        List<MeasuredControl> reach = ReadGroups(measurement.GetProperty("reach"));
        List<MeasuredControl> heightOnly = ReadGroups(measurement.GetProperty("heightOnly"));
        List<MeasuredControl> fontFloor = ReadGroups(measurement.GetProperty("fontFloor"));

        Dictionary<string, int> census = [];

        foreach (HandheldSelector selector in surface.ReachSelectors
            .Concat(surface.HeightOnlySelectors)
            .Concat(surface.FontFloorSelectors))
        {
            // Seeded at zero from the DECLARED set rather than counted up from what was found, which
            // is the whole point: a selector that matched nothing has to appear in the census as a
            // zero, and a census assembled from results can only ever list what exists.
            census[selector.Css] = 0;
        }

        foreach (MeasuredControl control in reach.Concat(heightOnly).Concat(fontFloor))
        {
            census[control.Selector] = census.GetValueOrDefault(control.Selector) + 1;
        }

        List<MeasuredControl> reachable = [];
        List<MeasuredControl> outOfReach = [];
        List<MeasuredControl> undersized = [];
        List<MeasuredControl> undersizedText = [];

        foreach (MeasuredControl control in reach)
        {
            bool inside = control.Left >= -PixelTolerance
                && control.Right <= clientWidth + PixelTolerance;

            (inside ? reachable : outOfReach).Add(control);

            if (control.Height < MinimumTouchTargetPixels - PixelTolerance)
            {
                undersized.Add(control);
            }
        }

        foreach (MeasuredControl control in heightOnly)
        {
            if (control.Height < MinimumTouchTargetPixels - PixelTolerance)
            {
                undersized.Add(control);
            }
        }

        foreach (MeasuredControl control in fontFloor)
        {
            // Strictly under, with no tolerance — see MinimumTextFontPixels for why this comparison
            // is not the one above with a different constant.
            if (control.FontSizePixels < MinimumTextFontPixels)
            {
                undersizedText.Add(control);
            }
        }

        HandheldReachReport report = new(
            surface,
            path,
            clientWidth,
            scrollWidth,
            widestOverflowHint,
            reachable,
            outOfReach,
            undersized,
            undersizedText,
            census);

        string[] silent = surface.ReachSelectors
            .Concat(surface.HeightOnlySelectors)
            .Concat(surface.FontFloorSelectors)
            .Where(selector => selector.MustMatch && census[selector.Css] == 0)
            .Select(selector => selector.Css)
            .ToArray();

        if (silent.Length > 0)
        {
            // Composed before the message rather than inside it. A nested literal in an interpolation
            // hole is legal in this language version and is the kind of line a reader has to parse
            // twice, which the standing habit in this directory already avoids for `await`.
            string named = string.Join("`, `", silent);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"On {surface.Name} at '{path}', {silent.Length} required selector(s) matched"
                    + $" nothing: `{named}`. Every verdict this barrier would report is therefore true"
                    + $" of a smaller page than the one that was meant to be measured — which is the"
                    + $" state `.record-actions button` was in for fifteen slices with nothing able to"
                    + $" say so. Either the arrangement did not build what it thinks it did, or a class"
                    + $" was renamed. The full census: {report.DescribeCensus()}."));
        }

        return report;
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

    private static List<MeasuredControl> ReadGroups(JsonElement groups)
    {
        List<MeasuredControl> controls = [];

        foreach (JsonElement group in groups.EnumerateArray())
        {
            string selector = group.GetProperty("selector").GetString() ?? "(unnamed selector)";

            foreach (JsonElement element in group.GetProperty("controls").EnumerateArray())
            {
                controls.Add(new MeasuredControl(
                    selector,
                    element.GetProperty("description").GetString() ?? "(unnamed element)",
                    element.GetProperty("left").GetDouble(),
                    element.GetProperty("right").GetDouble(),
                    element.GetProperty("height").GetDouble(),
                    element.GetProperty("fontSize").GetDouble()));
            }
        }

        return controls;
    }

    /// <summary>
    /// Reads the document's own width against what it needs, measures all three selector groups keeping
    /// each element attributed to the selector that found it, and — purely for the failure message —
    /// names the widest visible element that is not inside something that scrolls. Without that
    /// exclusion the answer on every administration page would be an area-link pill: correct, expected,
    /// and never the cause.
    ///
    /// <para><c>parseFloat</c> on the computed <c>fontSize</c> is safe without a unit check: a computed
    /// style resolves every length to pixels, so the string is always <c>NNpx</c>. It is read for every
    /// measured element rather than only for the font-floor group, because one shape of record read out
    /// of one shape of JSON is cheaper than two.</para>
    /// </summary>
    private const string MeasurementScript = """
        (groups) => {
            const root = document.documentElement;
            const clientWidth = root.clientWidth;

            const describe = (element) => {
                const tag = element.tagName.toLowerCase();
                const raw = typeof element.className === 'string' ? element.className.trim() : '';
                const classes = raw.length > 0 ? '.' + raw.split(/\s+/).join('.') : '';
                const label = element.getAttribute('aria-label') || (element.textContent || '');
                const name = label.replace(/\s+/g, ' ').trim().slice(0, 60);
                return name.length > 0 ? tag + classes + ' "' + name + '"' : tag + classes;
            };

            const measure = (selectors) => selectors.map((selector) => ({
                selector: selector,
                controls: Array.from(document.querySelectorAll(selector)).map((element) => {
                    const box = element.getBoundingClientRect();
                    return {
                        description: describe(element),
                        left: box.left,
                        right: box.right,
                        height: box.height,
                        fontSize: parseFloat(getComputedStyle(element).fontSize) || 0
                    };
                })
            }));

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
                reach: measure(groups[0]),
                heightOnly: measure(groups[1]),
                fontFloor: measure(groups[2])
            };
        }
        """;
}
