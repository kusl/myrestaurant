using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One line on the guest's committed order, as the surface renders it (§11.1): what it is, and which
/// of the two badges it is wearing. The badge is the whole of §16.3 scenario 6's assertion, so it is
/// modelled as a value rather than left as a class name for a scenario to match on.
/// </summary>
internal sealed record GuestOrderLine(string Name, GuestLineBadge Badge);

/// <summary>§11.1's two states for a live line, named the way the surface says them.</summary>
internal enum GuestLineBadge
{
    /// <summary>Sent, not yet fulfilled — the surface says "With the kitchen".</summary>
    WithTheKitchen,

    /// <summary>The kitchen marked it done — the surface says "At your table".</summary>
    AtYourTable,

    /// <summary>Taken off the order (§11.1's struck-through line).</summary>
    Removed,
}

/// <summary>
/// The guest ordering journeys the §16.3 scenarios walk: staging items into the basket, sending them,
/// and reading what came back (TECHNICAL_SPECIFICATION §6.5, §9, §11.1).
///
/// <para><b>Everything here is scoped to <c>#table-order-surface</c>, and that is not decoration.</b>
/// <c>/table/{id}</c> is a static-SSR page hosting this island, and the page itself renders a
/// <c>p.status-success</c> of its own — "You have joined Table Four" — the moment a join redirects back
/// to it. An unscoped wait for a success message would match that one every time and report a send as
/// having been accepted before the button was even pressed. The island's wrapper is the boundary
/// between what the circuit owns and what the HTTP response owns, so every selector below starts at
/// it.</para>
///
/// <para><b>Why the liveness wait exists.</b> Prerendering draws the whole surface — picker, basket,
/// totals — and every control on it is an <c>@onclick</c>. On an island that never became interactive
/// the taps below land on nothing at all, and the first thing a scenario would learn is that the basket
/// stayed empty thirty seconds later. <see cref="WaitForLiveSurfaceAsync"/> turns that into one sentence
/// about the circuit, at the moment the circuit was needed — the same lesson
/// <see cref="DisplayJourneys.WaitForLiveSurfaceAsync"/> was written for.</para>
/// </summary>
internal static class TableOrderJourneys
{
    /// <summary>The ordering island, whatever state it is in.</summary>
    private const string SurfaceSelector = "#table-order-surface";

    /// <summary>
    /// The island as rendered by a live circuit. <c>TableOrderSurface.razor</c> sets <c>data-live</c>
    /// from <c>RendererInfo.IsInteractive</c>, so this matches only markup an interactive renderer
    /// produced — never the prerendered pass, which is identical in every other respect.
    /// </summary>
    private const string LiveSurfaceSelector = "#table-order-surface[data-live='true']";

    /// <summary>Staged adds only. <c>.is-removal</c> rows are ticked removals and are counted separately.</summary>
    private const string BasketLineSelector =
        "#table-order-surface ul.order-basket li.order-basket-line:not(.is-removal)";

    /// <summary>The committed lines — what has actually reached the kitchen (§11.1's "Sent to the kitchen").</summary>
    private const string CommittedLineSelector = "#table-order-surface ul.order-lines li.order-line";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Waits until the ordering island on screen was rendered by a live circuit rather than by
    /// prerendering. Every other method here assumes it; a scenario calls it once after joining.
    /// </summary>
    internal static async Task WaitForLiveSurfaceAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(LiveSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            // Read the page BEFORE composing the message: an await inside an interpolated string that
            // binds to a handler is CS4007, and the diagnosis is worth more than the one-liner.
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The ordering surface never became interactive within"
                    + $" {timeout.TotalSeconds:F0}s; it is still the prerendered markup ({surface})."
                    + $" Nothing on this page will respond — Add to basket, Send and every quantity box"
                    + $" are @onclick handlers with no circuit behind them, and the kitchen will never"
                    + $" hear anything. Check that /_framework/blazor.web.js is served"
                    + $" (RestaurantInstance probes it at startup) and that the browser reached"
                    + $" /_blazor."),
                exception);
        }
    }

    /// <summary>
    /// Stages one item into the basket (§11.1) and returns once the basket has grown by one.
    ///
    /// <para>The item is chosen by <em>value</em> rather than by label. The picker's label is the name,
    /// the formatted price, and — for a deactivated item — §7's "(currently unavailable)"; matching on
    /// it would make this fail for a currency setting. The value is the bare identifier, which is what
    /// <see cref="AdministrationJourneys.CreateMenuItemAsync"/> hands back.</para>
    ///
    /// <para>Nothing reaches the kitchen here. §11.1 is explicit that the basket is local until Send,
    /// and the surface says so in as many words; <see cref="SendAsync"/> is the part that writes.</para>
    /// </summary>
    internal static async Task StageAsync(
        IPage page,
        MenuItemOnTheMenu item,
        int quantity,
        string? customizationNote = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(item);

        int before = await page.Locator(BasketLineSelector).CountAsync();

        await page.SelectOptionAsync(
            "#order-picker-item",
            new SelectOptionValue { Value = item.Identifier.ToString("D", CultureInfo.InvariantCulture) });

        await page.FillAsync(
            "#order-picker-quantity",
            quantity.ToString(CultureInfo.InvariantCulture));

        // Filled unconditionally, including with the empty string: the picker keeps whatever was typed
        // for the previous item until StageItem clears it, so skipping this would silently attach the
        // last note to the next thing staged.
        await page.FillAsync("#order-picker-note", customizationNote ?? string.Empty);

        // Focus the button before pressing it, and the order of the three fills above is not arbitrary
        // either. All three controls are @bind, which is @bind:event="onchange" by default, and Playwright
        // types into a text or number input rather than assigning to it — so the value is in the DOM but
        // the change event has not fired yet, and the component has not heard about it. Moving focus is
        // what fires it. Each fill blurs the previous field, and this focus blurs the last one, so all
        // three bindings have been dispatched by the time the click arrives. Relying on the click's own
        // implicit blur would work too, right up until the day the events raced.
        string addToBasket = $"{SurfaceSelector} .order-picker button:has-text('Add to basket')";

        await page.FocusAsync(addToBasket);
        await page.ClickAsync(addToBasket);

        if (await WaitForBasketCountAsync(page, before + 1, TimeSpan.FromSeconds(15)))
        {
            return;
        }

        // Both reads happen before the message is composed: an await inside an interpolated string that
        // binds to a handler is CS4007.
        int after = await page.Locator(BasketLineSelector).CountAsync();
        string refusal = await DescribeStagingRefusalAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Staging {quantity} × '{item.Name}' did not put a line in the basket:"
                + $" it holds {after} line(s) rather than {before + 1}. {refusal}"));
    }

    /// <summary>
    /// Presses Send (§11.1) and returns the confirmation the surface rendered — the sentence that names
    /// how many lines went, which is worth returning rather than merely waiting for because a send of
    /// two adds and a send of one read identically to a selector.
    ///
    /// <para>A refusal is §6.5.9's all-or-nothing panel: nothing was written, the basket is untouched,
    /// and each refused operation carries its own reason. Those reasons are the failure message here,
    /// because "Send did not confirm" without them is a mystery and with them is usually the answer.</para>
    /// </summary>
    internal static async Task<string> SendAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.ClickAsync($"{SurfaceSelector} .order-send button");

        // Scoped to the island: the parent page's own "You have joined …" success line is also a
        // p.status-success and is still on screen from the join that got us here.
        ILocator confirmation = page.Locator($"{SurfaceSelector} p.status-success").First;

        try
        {
            await confirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeSendRefusalAsync(page);

            throw new InvalidOperationException(
                $"The send was not confirmed. {refusal}",
                exception);
        }

        return (await confirmation.InnerTextAsync()).Trim();
    }

    /// <summary>How many staged adds are sitting in the basket right now.</summary>
    internal static Task<int> BasketLineCountAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Locator(BasketLineSelector).CountAsync();
    }

    /// <summary>
    /// The guest's committed lines, in the order the surface lists them (§11.1). Each carries the badge
    /// it is wearing, read from the chip's class rather than from its words: "At your table" is copy,
    /// <c>chip-ok</c> is the state.
    /// </summary>
    internal static async Task<IReadOnlyList<GuestOrderLine>> ReadCommittedLinesAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        List<GuestOrderLine> committed = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);

            // "2 × Soup of the day" — the quantity and the name are one span, deliberately, because
            // that is how §11.1 words it. The scenarios match on containment, so it is kept whole.
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            bool removed = await line.Locator("span.chip-warn").CountAsync() > 0;
            bool fulfilled = await line.Locator("span.chip-ok").CountAsync() > 0;

            GuestLineBadge badge = removed
                ? GuestLineBadge.Removed
                : fulfilled
                    ? GuestLineBadge.AtYourTable
                    : GuestLineBadge.WithTheKitchen;

            committed.Add(new GuestOrderLine(name, badge));
        }

        return committed;
    }

    /// <summary>
    /// Waits until the guest's committed lines satisfy <paramref name="expectation"/>, re-reading them
    /// as §9's broadcasts land. The predicate is the assertion — scenario 6 waits for a specific line to
    /// be wearing a specific badge, not merely for the list to have changed — and
    /// <paramref name="whatIsExpected"/> is what the failure says was wanted, beside what was on screen.
    /// </summary>
    internal static async Task<IReadOnlyList<GuestOrderLine>> WaitForCommittedLinesAsync(
        IPage page,
        Func<IReadOnlyList<GuestOrderLine>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<GuestOrderLine> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadCommittedLinesAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The guest's order never showed {whatIsExpected} within"
                + $" {timeout.TotalSeconds:F0}s. What it shows instead: {Describe(observed)}."));
    }

    /// <summary>A short, quotable rendering of a set of lines, for a failure message.</summary>
    internal static string Describe(IReadOnlyList<GuestOrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing at all"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{line.Name}' [{line.Badge}]")));
    }

    private static async Task<bool> WaitForBasketCountAsync(IPage page, int expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(BasketLineSelector).CountAsync() == expected)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    /// <summary>
    /// Whatever the picker has to say about why it would not stage the item — §11.1's staging notice,
    /// which is the surface refusing locally (an unavailable item, a quantity outside 1–100) rather than
    /// anything the server has been asked about yet.
    /// </summary>
    private static async Task<string> DescribeStagingRefusalAsync(IPage page)
    {
        ILocator notice = page.Locator($"{SurfaceSelector} .order-picker p.status-error");

        if (await notice.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The picker reports no refusal; the browser is at '{page.Url}'.");
        }

        string message = (await notice.First.InnerTextAsync()).Trim();

        return string.Create(CultureInfo.InvariantCulture, $"The picker says: {message}");
    }

    /// <summary>
    /// §6.5.9's refusal panel, flattened into one sentence: the headline plus every per-operation reason.
    /// A send is accepted or refused as a whole, so all of them are the diagnosis, not just the first.
    /// </summary>
    private static async Task<string> DescribeSendRefusalAsync(IPage page)
    {
        ILocator reasons = page.Locator($"{SurfaceSelector} ul.order-reject-list li");
        int count = await reasons.CountAsync();

        if (count == 0)
        {
            string surface = await DescribeSurfaceAsync(page);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"The surface shows no refusal either ({surface}), so the send may simply never have"
                + $" been dispatched; the browser is at '{page.Url}'.");
        }

        IReadOnlyList<string> all = await reasons.AllInnerTextsAsync();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Nothing was written and the surface refused it: {string.Join(" | ", all.Select(text => text.Trim()))}");
    }

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(SurfaceSelector);

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no ordering surface on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.First.GetAttributeAsync("data-live");

        return string.Create(CultureInfo.InvariantCulture, $"data-live='{live ?? "absent"}'");
    }
}
