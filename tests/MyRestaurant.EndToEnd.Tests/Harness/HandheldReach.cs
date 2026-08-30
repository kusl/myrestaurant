using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record HandheldSelector(string Css, bool MustMatch)
{
    internal static HandheldSelector Optional(string css) => new(css, MustMatch: false);

    internal static HandheldSelector Required(string css) => new(css, MustMatch: true);
}

internal sealed record MeasuredControl(
    string Selector,
    string Description,
    double Left,
    double Right,
    double Height,
    double Width,
    double FontSizePixels)
{
    internal bool IsCollapsed =>
        Width <= HandheldReach.PixelTolerance || Height <= HandheldReach.PixelTolerance;

    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Description} [left {Left:0.#}, right {Right:0.#}, width {Width:0.#},"
            + $" height {Height:0.#}, font {FontSizePixels:0.#}px, via `{Selector}`]");
}

internal sealed record HandheldSurface(
    string Name,
    string AnchorSelector,
    IReadOnlyList<HandheldSelector> ReachSelectors,
    IReadOnlyList<HandheldSelector> HeightOnlySelectors,
    IReadOnlyList<HandheldSelector> FontFloorSelectors,
    IReadOnlyList<HandheldSelector> ReachOnlySelectors)
{
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
            HandheldSelector.Optional(".page-head-areas a"),
        ],

        [],

        []);

    internal static HandheldSurface GuestOrder { get; } = new(
        "§11.1's guest ordering surface",

        "#table-order-surface[data-live='true'][data-loaded='true']",
        [
            HandheldSelector.Required("#table-order-surface button.order-menu-choice"),
            HandheldSelector.Required("#table-order-surface button.order-menu-inspect"),
            HandheldSelector.Required("#table-order-surface button.order-menu-like"),
            HandheldSelector.Required("#table-order-surface button.order-menu-comment-save"),
            HandheldSelector.Required("#table-order-surface button.order-menu-comment-withdraw"),
            HandheldSelector.Required("#table-order-surface textarea.order-menu-comment-body"),
            HandheldSelector.Required("#table-order-surface .order-picker .form-actions button"),
            HandheldSelector.Required("#table-order-surface .order-basket-controls button"),
            HandheldSelector.Required("#table-order-surface .order-send button"),
            HandheldSelector.Required("#table-order-surface .order-picker-quantity input"),
            HandheldSelector.Required("#table-order-surface .order-basket-quantity input"),
        ],
        [],
        [
            HandheldSelector.Required("#table-order-surface input[type=\"text\"]"),
            HandheldSelector.Required("#table-order-surface input[type=\"number\"]"),
            HandheldSelector.Required("#table-order-surface textarea"),
            HandheldSelector.Required("#table-order-surface .order-menu-detail-actions button"),
            HandheldSelector.Required("#table-order-surface .order-menu-comment-actions button"),
        ],
        [
            HandheldSelector.Required("#table-order-surface img.order-menu-thumbnail"),
            HandheldSelector.Required("#table-order-surface img.order-menu-detail-picture"),
        ]);

    internal static HandheldSurface CounterBoard { get; } = new(
        "§11.3's counter board",

        "#counter-board-surface[data-live='true'][data-loaded='true']",
        [
            HandheldSelector.Required("#counter-board-surface .counter-sitting-actions a"),
        ],
        [],
        [],
        []);

    internal static HandheldSurface CounterBill { get; } = new(
        "§11.3's bill at the till",

        "#counter-sitting-surface[data-live='true'][data-loaded='true']",
        [
            HandheldSelector.Required("#counter-sitting-surface .counter-line-actions button"),
            HandheldSelector.Required("#counter-sitting-surface .counter-add .form-actions button"),
            HandheldSelector.Required("#counter-sitting-surface .counter-settle .form-actions button"),
            HandheldSelector.Required("#counter-sitting-surface .counter-settle .form-actions a"),
        ],
        [],
        [
            HandheldSelector.Required("#counter-sitting-surface .counter-add select"),
            HandheldSelector.Required("#counter-sitting-surface .counter-add input"),
            HandheldSelector.Required("#counter-sitting-surface .counter-add button"),
            HandheldSelector.Required("#counter-sitting-surface .counter-settle button"),
            HandheldSelector.Required("#counter-sitting-surface .counter-line button"),
        ],
        []);
}

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
    internal int MeasuredCount => Reachable.Count + OutOfReach.Count;

    internal bool ScrollsSideways => ScrollWidth > ClientWidth + HandheldReach.PixelTolerance;

    internal string DescribeOverflow()
    {
        string hint = WidestOverflowHint ?? "not identified";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Path} lays out {ScrollWidth:0.#}px wide inside a {ClientWidth:0.#}px viewport"
                + $" (widest element outside a scroll container: {hint})");
    }

    internal string DescribeCensus()
        => string.Join(
            ", ",
            Census.Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"`{entry.Key}` × {entry.Value}")));
}

internal static class HandheldReach
{
    internal const double PixelTolerance = 1.0;

    internal const double MinimumTouchTargetPixels = 44.0;

    internal const double MinimumTextFontPixels = 16.0;

    private static readonly TimeSpan SurfacePatience = TimeSpan.FromSeconds(30);

    internal static Task<HandheldReachReport> MeasureAsync(IPage page, string path)
        => MeasureAsync(page, path, HandheldSurface.Administration);

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
            [.. surface.ReachOnlySelectors.Select(selector => selector.Css)],
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
        List<MeasuredControl> reachOnly = ReadGroups(measurement.GetProperty("reachOnly"));

        Dictionary<string, int> census = [];

        foreach (HandheldSelector selector in AllSelectors(surface))
        {
            census[selector.Css] = 0;
        }

        foreach (MeasuredControl control in reach
            .Concat(heightOnly)
            .Concat(fontFloor)
            .Concat(reachOnly))
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

        foreach (MeasuredControl control in reachOnly)
        {
            bool inside = control.Left >= -PixelTolerance
                && control.Right <= clientWidth + PixelTolerance;

            (inside ? reachable : outOfReach).Add(control);
        }

        foreach (MeasuredControl control in fontFloor)
        {
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

        string[] silent = AllSelectors(surface)
            .Where(selector => selector.MustMatch && census[selector.Css] == 0)
            .Select(selector => selector.Css)
            .ToArray();

        if (silent.Length > 0)
        {
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

        MeasuredControl[] collapsed = reachOnly
            .Where(control => control.IsCollapsed)
            .Where(control => surface.ReachOnlySelectors
                .Any(selector => selector.MustMatch
                    && string.Equals(selector.Css, control.Selector, StringComparison.Ordinal)))
            .ToArray();

        if (collapsed.Length > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"On {surface.Name} at '{path}', {collapsed.Length} required element(s) are in the"
                    + $" document with no area at all: {Format(collapsed)}. An <img> whose bytes have"
                    + $" not arrived reports a 0×0 box, which lies inside every viewport there is and"
                    + $" appears in the census as a one — so every verdict here would be true of a"
                    + $" placeholder rather than of a picture. Arrange the decode before measuring"
                    + $" (`MenuPictureJourneys.WaitForDecodedAsync`), or §7's route is not answering."
                    + $" The full census: {report.DescribeCensus()}."));
        }

        return report;
    }

    private static IEnumerable<HandheldSelector> AllSelectors(HandheldSurface surface)
        => surface.ReachSelectors
            .Concat(surface.HeightOnlySelectors)
            .Concat(surface.FontFloorSelectors)
            .Concat(surface.ReachOnlySelectors);

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
                    element.GetProperty("width").GetDouble(),
                    element.GetProperty("fontSize").GetDouble()));
            }
        }

        return controls;
    }

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
                        width: box.width,
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
                fontFloor: measure(groups[2]),
                reachOnly: measure(groups[3])
            };
        }
        """;
}
