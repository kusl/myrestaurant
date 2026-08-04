using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Orders;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One line on a bill at the till, as §11.3 renders it.
///
/// <para><b>Both money fields are text, deliberately.</b> They are rendered through
/// <c>MoneyText.Format(amount, CurrencyCode)</c>, and parsing them back into decimals here would mean
/// reimplementing a currency formatter inside a test in order to compare against a number the test
/// already knew. A scenario formats its expectation the same way the surface did — see
/// <see cref="RestaurantInstance.CurrencyCode"/> — and compares strings, which is a stricter assertion
/// than comparing decimals because it catches a formatter that has started dropping a symbol.</para>
///
/// <para><paramref name="UnitPriceText"/> is the one §16.3 scenario 9 turns on. A price adjustment
/// (§6.5.7) changes the <em>unit</em> price, and the extension is recomputed from it — so a line at
/// quantity two is the only shape in which "the adjustment landed" and "the bill was recalculated" are
/// separable claims.</para>
/// </summary>
internal sealed record CounterBillLine(
    int Quantity,
    string Name,
    string LineTotalText,
    string UnitPriceText,
    string? Note,
    bool IsDelivered);

/// <summary>One person's part of a bill (§8.3's <c>sitting_bill</c> grouping) with their lines under it.</summary>
internal sealed record CounterBillEntry(
    string BillName,
    string PersonTotalText,
    IReadOnlyList<CounterBillLine> Lines);

/// <summary>
/// A whole bill at the till, as one instant of §11.3.
///
/// <para><paramref name="RunningTotalText"/> is the header figure — <c>CounterSittingSummary.AmountToShow</c>,
/// which for an open sitting is the running total. It is read here rather than the settle panel's
/// "Table total" because the two are computed by different code on different sides of the screen: the
/// header comes straight from the <c>sitting_bill</c> view, in SQL, while the settle panel sums the
/// per-person entries in C#. The SQL one is the genuinely independent opinion when the thing being
/// checked against it is a guest's own event fold.</para>
/// </summary>
internal sealed record CounterBill(
    string TableLabel,
    string RunningTotalText,
    IReadOnlyList<CounterBillEntry> People);

/// <summary>
/// The journeys a counter walks at the till: finding an open table on the board, opening its bill, and
/// adjusting a price with a reason (TECHNICAL_SPECIFICATION §5.3, §6.5.7, §11.3).
///
/// <para><b>Every surface here needs a circuit, and none of them says so on its own.</b>
/// <c>/counter</c> and <c>/counter/sittings/{id}</c> are interactive-server pages rather than static
/// SSR, and every control on the second one — Adjust price, Remove, Add to the bill, Close &amp; settle
/// — is an <c>@onclick</c>. A prerendered till is the dangerous kind of broken because it is the kind
/// that looks right: the bill is correct as of the request, every total adds up, and pressing anything
/// does nothing at all. So <see cref="OpenSittingAsync"/> waits on <c>data-live</c>, published by
/// <c>CounterSitting.razor</c> as of M6 Slice 12 for exactly this reason.</para>
///
/// <para><b>Why the board's link is followed rather than typed.</b> A scenario knows the sitting
/// identifier only if it reads the database for it, and §16.3's "counter adjusts a price" means the
/// counter found the table — the board, the open-sittings query, and the link. Following it also means
/// the scenario can cross-check the identifier it landed on against the row, which is how "opened the
/// right sitting" is told apart from "opened a sitting". The click goes through
/// <see cref="EnhancedNavigation"/> because <c>#counter-sitting-surface</c> is genuinely absent from
/// the board, which makes it an exact barrier rather than a delay.</para>
///
/// <para><b>Why an adjustment is judged by the unit price rather than by the confirmation.</b> §11.3
/// writes a flash sentence naming the new price, and that sentence survives until something clears it —
/// so a second adjustment of the same shape is satisfied by the first one's words. The unit price on
/// the line is the state the transaction actually wrote, re-read from <c>order_current_line</c>, and it
/// cannot be left over from anything. A refusal ends the wait immediately with the surface's own reason,
/// because every button here goes through <c>IOrderWorkflow</c> and can be refused under the §6.6 lock
/// — a guest sending, the kitchen fulfilling, somebody closing a second earlier — and the board renders
/// that refusal rather than throwing.</para>
/// </summary>
internal static class CounterJourneys
{
    /// <summary>The board's route. <c>CounterBoard.razor</c> is <c>@page "/counter"</c>.</summary>
    internal const string BoardPath = "/counter";

    private const string BoardSurfaceSelector = "section.counter-board";

    /// <summary>One open table on the board (§11.3). Settled ones are rows in a list, not articles.</summary>
    private const string OpenSittingSelector = "section.counter-board article.counter-sitting";

    private const string SittingSurfaceSelector = "#counter-sitting-surface";

    /// <summary>
    /// The bill as rendered by a live circuit. <c>CounterSitting.razor</c> sets <c>data-live</c> from
    /// <c>RendererInfo.IsInteractive</c>, so this matches only markup an interactive renderer produced —
    /// never the prerendered pass, which is identical in every other respect.
    /// </summary>
    private const string LiveSittingSurfaceSelector = "#counter-sitting-surface[data-live='true']";

    private const string BillEntrySelector = "#counter-sitting-surface article.counter-person";
    private const string BillLineSelector = "li.counter-line";

    /// <summary>The two ids <c>CounterSitting.razor</c>'s price editor carries (M6 Slice 12).</summary>
    private const string AdjustPriceFieldSelector = "#counter-adjust-price";
    private const string AdjustReasonFieldSelector = "#counter-adjust-reason";

    /// <summary>The path prefix a sitting's own URL starts with, for recovering the identifier.</summary>
    private const string SittingPathPrefix = "/counter/sittings/";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long one press of Adjust has to produce an answer. One transaction against a local PostgreSQL
    /// under an advisory lock nothing else is holding, so the honest expectation is milliseconds; thirty
    /// seconds is the same patience every other page operation in this harness gets. Either outcome ends
    /// the wait, so this length is only ever reached when the click did not dispatch at all.
    /// </summary>
    private static readonly TimeSpan AdjustmentPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens the bill for the table labelled <paramref name="tableLabel"/> from the counter board and
    /// returns the sitting identifier the URL landed on, once a circuit is behind the page.
    ///
    /// <para>The table is found by the heading on its card rather than by putting the label into a CSS
    /// selector: labels are free text, an apostrophe in "Chef's table" would break a
    /// <c>:text-is('…')</c> selector, and a scenario is not the place to learn about selector
    /// escaping.</para>
    /// </summary>
    internal static async Task<Guid> OpenSittingAsync(IPage page, string tableLabel, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(BoardPath);
        await WaitForBoardAsync(page, timeout);

        ILocator cards = page.Locator(OpenSittingSelector);
        int count = await cards.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator card = cards.Nth(index);
            string label = (await card.Locator("h2").First.InnerTextAsync()).Trim();

            if (!string.Equals(label, tableLabel, StringComparison.Ordinal))
            {
                continue;
            }

            await EnhancedNavigation.FollowAsync(
                page,
                card.Locator("a:has-text('Bill')").First,
                SittingSurfaceSelector,
                "the sitting's bill at the till",
                timeout);

            await WaitForLiveSittingAsync(page, timeout);

            return SittingIdentifierFrom(page.Url);
        }

        // Read before composing: an await inside an interpolation hole of a string that binds to
        // DefaultInterpolatedStringHandler is CS4007, because the handler is a ref struct.
        string board = await DescribeBoardAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The counter board has no open table labelled '{tableLabel}'. §5.1 opens a sitting on"
                + $" the first join and §11.3 lists every open one, so either nobody has joined that"
                + $" table or it has already been settled. What the board shows: {board}."));
    }

    /// <summary>
    /// Waits until the counter board on screen was rendered by a live circuit. A board that never
    /// became interactive lists the floor as it stood at the moment of the request and then never
    /// changes — which for a screen whose whole job is to show a total moving is the failure that looks
    /// most like success.
    /// </summary>
    internal static async Task WaitForBoardAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(BoardSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The counter board never rendered within {timeout.TotalSeconds:F0}s. §3.7 admits"
                    + $" counter and administrator to /counter, so a principal that failed the policy"
                    + $" would be looking at the access-denied panel instead; the browser is at"
                    + $" '{page.Url}'."),
                exception);
        }
    }

    /// <summary>
    /// Waits until the bill on screen was rendered by a live circuit rather than by prerendering. Every
    /// other method here assumes it.
    /// </summary>
    internal static async Task WaitForLiveSittingAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(LiveSittingSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The till never became interactive within {timeout.TotalSeconds:F0}s; it is still"
                    + $" the prerendered markup ({surface}). The bill will read correctly and every"
                    + $" control on it will do nothing — Adjust price, Remove, Add to the bill and"
                    + $" Close & settle are all @onclick handlers with no circuit behind them, and the"
                    + $" screen will not hear §9 either. Check that /_framework/blazor.web.js is served"
                    + $" (RestaurantInstance probes it at startup) and that the browser reached"
                    + $" /_blazor."),
                exception);
        }
    }

    /// <summary>
    /// Adjusts one line's unit price with a reason — §11.3's "price adjustment dialog (new price +
    /// required reason)" — and returns once the bill itself shows the new unit price.
    ///
    /// <para>The price is typed invariantly, because <c>CounterSitting.razor</c> parses it invariantly:
    /// the amount is a <c>numeric(10,2)</c>, and which separator the container's locale happens to use is
    /// not a decision anybody made about this restaurant.</para>
    ///
    /// <para>Both fields are <c>@bind:event="oninput"</c>, so no blur is needed to dispatch them — unlike
    /// the guest's picker, where the default <c>onchange</c> is why
    /// <see cref="TableOrderJourneys.StageAsync"/> has to move focus before clicking.</para>
    /// </summary>
    internal static async Task AdjustPriceAsync(
        IPage page,
        string menuItemName,
        decimal newUnitPrice,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator line = await LocateLineAsync(page, menuItemName, "adjust");

        await line.Locator("button:has-text('Adjust price')").First.ClickAsync();

        ILocator priceField = page.Locator(AdjustPriceFieldSelector);

        try
        {
            await priceField.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeRefusalAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pressing Adjust price on '{menuItemName}' did not open the editor. §11.3 renders"
                    + $" the line's controls only while the sitting is open, so a settled sitting offers"
                    + $" none of them (§6.5.8 admits nothing but an administrator's corrective events"
                    + $" after a close). {refusal}"),
                exception);
        }

        string expectedUnitPrice = Money(newUnitPrice);

        await priceField.FillAsync(newUnitPrice.ToString("0.00", CultureInfo.InvariantCulture));
        await page.FillAsync(AdjustReasonFieldSelector, reason);

        await page.ClickAsync($"{SittingSurfaceSelector} .counter-editor button:has-text('Adjust')");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + AdjustmentPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // The refusal is looked for first. §11.3 shows both a notice and a problem through the same
            // re-read, and an adjustment that was refused under the §6.6 lock leaves the unit price
            // exactly as it was — so a poll that only watched the price would spend the whole patience
            // failing to notice that the answer had already arrived.
            IReadOnlyList<string> refusals = await ReadRefusalReasonsAsync(page);

            if (refusals.Count > 0)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Adjusting '{menuItemName}' to {expectedUnitPrice} was refused, so nothing was"
                        + $" written (§6.5.9 is all-or-nothing at the granularity of the event). The till"
                        + $" says: {string.Join(" | ", refusals)}"));
            }

            CounterBillLine? current = await FindLineAsync(page, menuItemName);

            if (current is not null
                && string.Equals(current.UnitPriceText, expectedUnitPrice, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        string bill = Describe(await ReadBillAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Adjusting '{menuItemName}' to {expectedUnitPrice} each neither took effect nor was"
                + $" refused within {AdjustmentPatience.TotalSeconds:F0}s, so the click may never have"
                + $" been dispatched at all. The bill holds: {bill}."));
    }

    /// <summary>The whole bill on screen right now, read in one pass (§11.3).</summary>
    internal static async Task<CounterBill> ReadBillAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator surface = page.Locator(SittingSurfaceSelector).First;

        string tableLabel = (await surface.Locator("h1").First.InnerTextAsync()).Trim();
        string runningTotal =
            (await surface.Locator("span.counter-detail-total-amount").First.InnerTextAsync()).Trim();

        ILocator entries = page.Locator(BillEntrySelector);
        int entryCount = await entries.CountAsync();

        List<CounterBillEntry> people = new(entryCount);

        for (int index = 0; index < entryCount; index++)
        {
            ILocator entry = entries.Nth(index);

            string billName = (await entry.Locator("h2").First.InnerTextAsync()).Trim();
            string personTotal =
                (await entry.Locator("span.counter-person-total").First.InnerTextAsync()).Trim();

            ILocator lines = entry.Locator(BillLineSelector);
            int lineCount = await lines.CountAsync();

            List<CounterBillLine> theirLines = new(lineCount);

            for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                theirLines.Add(await ReadLineAsync(lines.Nth(lineIndex)));
            }

            people.Add(new CounterBillEntry(billName, personTotal, theirLines));
        }

        return new CounterBill(tableLabel, runningTotal, people);
    }

    /// <summary>A short, quotable rendering of a bill, for a failure message.</summary>
    internal static string Describe(CounterBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        if (bill.People.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"'{bill.TableLabel}' at {bill.RunningTotalText}, nobody has ordered");
        }

        string people = string.Join(
            " | ",
            bill.People.Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"'{entry.BillName}' {entry.PersonTotalText}: {DescribeLines(entry.Lines)}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{bill.TableLabel}' at {bill.RunningTotalText} — {people}");
    }

    /// <summary>A short, quotable rendering of a set of bill lines, for a failure message.</summary>
    internal static string DescribeLines(IReadOnlyList<CounterBillLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing on this order right now"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{line.Quantity} × '{line.Name}' {line.LineTotalText} ({line.UnitPriceText} each)"
                    + $" [{(line.IsDelivered ? "delivered" : "with the kitchen")}]")));
    }

    // --- internals ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads one bill line. The two money figures live in one element and are separated here rather than
    /// at the two selectors, because §11.3 nests the unit price <em>inside</em> the price block —
    /// <c>span.counter-line-price</c> contains both "$22.00" and its child
    /// <c>span.counter-line-unit</c>'s "$11.00 each", so its own inner text carries them together.
    /// Removing the child's text from the parent's is exact and does not depend on how a flex column
    /// happens to be turned into line breaks.
    /// </summary>
    private static async Task<CounterBillLine> ReadLineAsync(ILocator line)
    {
        // "2×" — the multiplication sign is markup rather than data, so it is trimmed off rather than
        // parsed around. The same treatment KitchenJourneys gives the kitchen's own quantity.
        string quantityText = (await line.Locator("span.counter-line-quantity").First.InnerTextAsync())
            .Trim()
            .TrimEnd('×');

        string name = (await line.Locator("span.counter-line-name").First.InnerTextAsync()).Trim();

        string priceBlock = (await line.Locator("span.counter-line-price").First.InnerTextAsync()).Trim();
        string unitBlock = (await line.Locator("span.counter-line-unit").First.InnerTextAsync()).Trim();

        ILocator note = line.Locator("p.counter-line-note");

        string? noteText = await note.CountAsync() > 0
            ? (await note.First.InnerTextAsync()).Trim()
            : null;

        // §11.3 renders a delivered line's chip as .chip-ok and a pending one's as .chip-warn, which is
        // the state rather than the copy beside it.
        bool delivered = await line.Locator("span.chip-ok").CountAsync() > 0;

        return new CounterBillLine(
            int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity)
                ? quantity
                : 0,
            name,
            WithoutUnitPrice(priceBlock, unitBlock),
            WithoutEachSuffix(unitBlock),
            noteText,
            delivered);
    }

    /// <summary>The line total alone, with the nested unit-price text removed from the price block.</summary>
    private static string WithoutUnitPrice(string priceBlock, string unitBlock)
    {
        if (unitBlock.Length == 0)
        {
            return priceBlock;
        }

        int at = priceBlock.LastIndexOf(unitBlock, StringComparison.Ordinal);

        return at < 0 ? priceBlock : priceBlock[..at].Trim();
    }

    /// <summary>"$11.00 each" → "$11.00", so a scenario compares against a formatted amount.</summary>
    private static string WithoutEachSuffix(string unitBlock)
    {
        const string suffix = "each";

        return unitBlock.EndsWith(suffix, StringComparison.Ordinal)
            ? unitBlock[..^suffix.Length].Trim()
            : unitBlock;
    }

    /// <summary>
    /// The <c>li.counter-line</c> for the named item, or a failure naming what the bill holds instead.
    /// Matched by reading the names rather than by selector text, for the escaping reason in the type
    /// remarks.
    /// </summary>
    private static async Task<ILocator> LocateLineAsync(IPage page, string menuItemName, string verb)
    {
        ILocator lines = page.Locator($"{SittingSurfaceSelector} {BillLineSelector}");
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.counter-line-name").First.InnerTextAsync()).Trim();

            if (string.Equals(name, menuItemName, StringComparison.Ordinal))
            {
                return line;
            }
        }

        string bill = Describe(await ReadBillAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no line for '{menuItemName}' on this bill to {verb}. It holds: {bill}."));
    }

    /// <summary>The named line as the bill currently renders it, or <c>null</c> when it is not there.</summary>
    private static async Task<CounterBillLine?> FindLineAsync(IPage page, string menuItemName)
    {
        CounterBill bill = await ReadBillAsync(page);

        return bill.People
            .SelectMany(entry => entry.Lines)
            .FirstOrDefault(line => string.Equals(line.Name, menuItemName, StringComparison.Ordinal));
    }

    /// <summary>
    /// §11.3's refusal, as the list of reasons it names. The problem sentence comes first and §6.5.9's
    /// per-operation reasons follow it, because a refusal names every reason rather than only the first —
    /// and an empty list is the honest answer when the till is not refusing anything.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadRefusalReasonsAsync(IPage page)
    {
        List<string> reasons = [];

        ILocator problem = page.Locator($"{SittingSurfaceSelector} p.status-error");

        if (await problem.CountAsync() > 0)
        {
            reasons.Add((await problem.First.InnerTextAsync()).Trim());
        }

        ILocator perOperation = page.Locator($"{SittingSurfaceSelector} ul.counter-rejection li");

        if (await perOperation.CountAsync() > 0)
        {
            IReadOnlyList<string> all = await perOperation.AllInnerTextsAsync();
            reasons.AddRange(all.Select(text => text.Trim()));
        }

        return reasons;
    }

    private static async Task<string> DescribeRefusalAsync(IPage page)
    {
        IReadOnlyList<string> reasons = await ReadRefusalReasonsAsync(page);

        if (reasons.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The till reports no refusal; the browser is at '{page.Url}'.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The till says: {string.Join(" | ", reasons)}");
    }

    private static async Task<string> DescribeBoardAsync(IPage page)
    {
        ILocator cards = page.Locator(OpenSittingSelector);
        int count = await cards.CountAsync();

        if (count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no open table at all; the browser is at '{page.Url}'");
        }

        List<string> described = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator card = cards.Nth(index);
            string label = (await card.Locator("h2").First.InnerTextAsync()).Trim();
            string amount = (await card.Locator("div.counter-sitting-amount").First.InnerTextAsync()).Trim();

            described.Add(string.Create(CultureInfo.InvariantCulture, $"'{label}' at {amount}"));
        }

        return string.Join("; ", described);
    }

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(SittingSurfaceSelector);

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no bill on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.First.GetAttributeAsync("data-live");

        return string.Create(CultureInfo.InvariantCulture, $"data-live='{live ?? "absent"}'");
    }

    /// <summary>
    /// The sitting identifier out of a <c>/counter/sittings/{id}</c> URL. Parsed rather than trusted:
    /// under enhanced navigation the address bar can be ahead of the document, so a scenario that means
    /// to cross-check which sitting it opened deserves to be told when the URL is not one at all.
    /// </summary>
    private static Guid SittingIdentifierFrom(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && parsed.AbsolutePath.StartsWith(SittingPathPrefix, StringComparison.Ordinal)
            && Guid.TryParse(parsed.AbsolutePath[SittingPathPrefix.Length..], out Guid sittingIdentifier))
        {
            return sittingIdentifier;
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Following a table's bill link landed on '{url}', which is not a"
                + $" {SittingPathPrefix}{{id}} URL."));
    }

    private static string Money(decimal amount)
        => MoneyText.Format(amount, RestaurantInstance.CurrencyCode);
}
