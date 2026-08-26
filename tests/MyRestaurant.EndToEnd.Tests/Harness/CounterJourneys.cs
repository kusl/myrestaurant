using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Orders;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record CounterBillLine(
    int Quantity,
    string Name,
    string LineTotalText,
    string UnitPriceText,
    string? Note,
    bool IsDelivered);

internal sealed record CounterBillEntry(
    string BillName,
    string PersonTotalText,
    IReadOnlyList<CounterBillLine> Lines);

internal sealed record CounterBill(
    string TableLabel,
    string RunningTotalText,
    IReadOnlyList<CounterBillEntry> People);

internal sealed record CounterPendingWarning(int LineCount, string Sentence);

internal sealed record CloseConfirmation(string AmountText, string Sentence);

internal sealed record SettledTill(
    string TotalLabel,
    string TotalText,
    string TableTotalText,
    string HeaderMeta,
    string? Notice,
    bool SaysReadOnly,
    bool ShowsCorrection,
    int LineControlCount,
    bool OffersClose,
    bool OffersStaffAdd);

internal sealed record SettledTableRow(string TableLabel, string AmountText, string SettledBy);

internal sealed record CounterFloor(
    IReadOnlyList<string> OpenTableLabels,
    IReadOnlyList<SettledTableRow> Settled);

internal static class CounterJourneys
{
    internal const string BoardPath = "/counter";

    private const string BoardSurfaceSelector =
        "#counter-board-surface[data-live='true'][data-loaded='true']";

    private const string BoardSurfaceAnyStateSelector = "#counter-board-surface";

    private const string OpenSittingSelector = "section.counter-board article.counter-sitting";

    private const string SittingSurfaceSelector = "#counter-sitting-surface";

    private const string LiveSittingSurfaceSelector =
        "#counter-sitting-surface[data-live='true'][data-loaded='true']";

    private const string BillEntrySelector = "#counter-sitting-surface article.counter-person";
    private const string BillLineSelector = "li.counter-line";

    private const string AdjustPriceFieldSelector = "#counter-adjust-price";
    private const string AdjustReasonFieldSelector = "#counter-adjust-reason";

    private const string CloseButtonSelector = "#counter-close";
    private const string ConfirmCloseButtonSelector = "#counter-close-confirm";

    private const string PendingWarningSelector = "#counter-sitting-surface p.counter-pending-warning";
    private const string CloseConfirmSelector = "#counter-sitting-surface p.counter-settle-confirm";

    private const string ReadOnlyNoteSelector = "#counter-sitting-surface p.counter-readonly";

    private const string TotalLabelSelector = "#counter-sitting-surface span.counter-detail-total-label";
    private const string TotalAmountSelector = "#counter-sitting-surface span.counter-detail-total-amount";
    private const string CorrectedTotalSelector = "#counter-sitting-surface span.counter-detail-corrected";
    private const string SettlePanelTotalSelector = "#counter-sitting-surface .counter-settle-total strong";
    private const string HeaderMetaSelector = "#counter-sitting-surface p.counter-detail-meta";
    private const string NoticeSelector = "#counter-sitting-surface p.status-success";
    private const string LineActionsSelector = "#counter-sitting-surface div.counter-line-actions";
    private const string StaffAddSelector = "#counter-sitting-surface section.counter-add";

    private const string SettledRowSelector = "section.counter-board li.counter-settled-row";

    private const string SittingPathPrefix = "/counter/sittings/";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan AdjustmentPatience = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ClosePatience = TimeSpan.FromSeconds(30);

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

        string board = await DescribeBoardAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The counter board has no open table labelled '{tableLabel}'. §5.1 opens a sitting on"
                + $" the first join and §11.3 lists every open one, so either nobody has joined that"
                + $" table or it has already been settled. What the board shows: {board}."));
    }

    internal static async Task OpenSettledSittingAsync(
        IPage page,
        Guid sittingIdentifier,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(PathFor(sittingIdentifier));
        await WaitForLiveSittingAsync(page, timeout);

        try
        {
            await page.Locator(ReadOnlyNoteSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            string settle = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The till opened sitting {sittingIdentifier:D} but does not say it is settled."
                    + $" §11.3 renders the read-only note from !_sitting.IsOpen — that is, from"
                    + $" closed_at being set on the row — so either this sitting is still open or the"
                    + $" identifier names a different one. {settle}"),
                exception);
        }
    }

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
            string surface = await DescribeBoardSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The counter board was not live and loaded within {timeout.TotalSeconds:F0}s"
                    + $" ({surface}). §3.7 admits counter and administrator to /counter, so a principal"
                    + $" that failed the policy would be looking at the access-denied panel and the"
                    + $" surface would be absent entirely; a surface that is present with"
                    + $" data-live='false' never got a circuit, and one stuck at data-loaded='false' is"
                    + $" waiting on §11.3's two queries."),
                exception);
        }
    }

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
                    $"The till was not live and loaded within {timeout.TotalSeconds:F0}s ({surface})."
                    + $" A surface present with data-live='false' is still the prerendered markup: the"
                    + $" bill will read correctly and every control on it will do nothing — Adjust"
                    + $" price, Remove, Add to the bill and Close & settle are all @onclick handlers"
                    + $" with no circuit behind them, and the screen will not hear §9 either. Check that"
                    + $" /_framework/blazor.web.js is served (RestaurantInstance probes it at startup)"
                    + $" and that the browser reached /_blazor. One stuck at data-loaded='false' has a"
                    + $" circuit and is still on §11.3's reads."),
                exception);
        }
    }

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

    internal static async Task<CounterPendingWarning?> ReadPendingWarningAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator warning = page.Locator(PendingWarningSelector);

        if (await warning.CountAsync() == 0)
        {
            return null;
        }

        string sentence = (await warning.First.InnerTextAsync()).Trim();
        string headline = (await warning.First.Locator("strong").First.InnerTextAsync()).Trim();

        return new CounterPendingWarning(LeadingCount(headline), sentence);
    }

    internal static async Task<CloseConfirmation> BeginCloseAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator close = page.Locator(CloseButtonSelector);

        try
        {
            await close.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The till is not offering Close & settle. §11.3 renders it only while the sitting is"
                    + $" open, so the likeliest cause is that this sitting has already been settled —"
                    + $" by an end-of-day pass (§5.4) or by somebody else at another till. {surface}"),
                exception);
        }

        await close.First.ClickAsync();

        ILocator prompt = page.Locator(CloseConfirmSelector);

        try
        {
            await prompt.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pressing Close & settle did not raise §11.3's confirmation. The button is an"
                    + $" @onclick that sets one field, so nothing can refuse it — if the prompt is absent"
                    + $" the click was not dispatched, which means no circuit. {surface}"),
                exception);
        }

        string sentence = (await prompt.First.InnerTextAsync()).Trim();
        ILocator amount = prompt.First.Locator("strong");

        if (await amount.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§11.3's confirmation names no amount, so nobody pressing Yes would know what they"
                    + $" were agreeing to. It reads: '{sentence}'."));
        }

        return new CloseConfirmation((await amount.First.InnerTextAsync()).Trim(), sentence);
    }

    internal static async Task<SettledTill> ConfirmCloseAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator confirm = page.Locator(ConfirmCloseButtonSelector);

        try
        {
            await confirm.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no confirmation to accept — §11.3's prompt is not on screen, so"
                    + $" BeginCloseAsync either was not called or its prompt has since been abandoned."
                    + $" {surface}"),
                exception);
        }

        await confirm.First.ClickAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(ReadOnlyNoteSelector).CountAsync() > 0)
            {
                return await ReadSettledTillAsync(page);
            }

            IReadOnlyList<string> refusals = await ReadRefusalReasonsAsync(page);

            if (refusals.Count > 0)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The close was refused and the sitting is still open, so no total was stamped."
                        + $" The till says: {string.Join(" | ", refusals)}"));
            }

            await Task.Delay(PollInterval);
        }

        string settle = await DescribeSettleSectionAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The sitting neither settled nor refused within {timeout.TotalSeconds:F0}s. §5.3 takes"
                + $" FOR UPDATE on the sitting row and §6.6 has every order writer take FOR SHARE on the"
                + $" same one, so a genuinely contended close waits — but nothing else here is writing."
                + $" {settle}"));
    }

    internal static async Task<SettledTill> ReadSettledTillAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator notice = page.Locator(NoticeSelector);

        return new SettledTill(
            await ScreenText.DeclaredAsync(page.Locator(TotalLabelSelector).First),
            (await page.Locator(TotalAmountSelector).First.InnerTextAsync()).Trim(),
            (await page.Locator(SettlePanelTotalSelector).First.InnerTextAsync()).Trim(),
            (await page.Locator(HeaderMetaSelector).First.InnerTextAsync()).Trim(),
            await notice.CountAsync() > 0 ? (await notice.First.InnerTextAsync()).Trim() : null,
            await page.Locator(ReadOnlyNoteSelector).CountAsync() > 0,
            await page.Locator(CorrectedTotalSelector).CountAsync() > 0,
            await page.Locator(LineActionsSelector).CountAsync(),
            await page.Locator(CloseButtonSelector).CountAsync() > 0
                || await page.Locator(ConfirmCloseButtonSelector).CountAsync() > 0,
            await page.Locator(StaffAddSelector).CountAsync() > 0);
    }

    internal static async Task<CounterFloor> ReadFloorAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator cards = page.Locator(OpenSittingSelector);
        int openCount = await cards.CountAsync();

        List<string> open = new(openCount);

        for (int index = 0; index < openCount; index++)
        {
            open.Add((await cards.Nth(index).Locator("h2").First.InnerTextAsync()).Trim());
        }

        ILocator rows = page.Locator(SettledRowSelector);
        int settledCount = await rows.CountAsync();

        List<SettledTableRow> settled = new(settledCount);

        for (int index = 0; index < settledCount; index++)
        {
            ILocator row = rows.Nth(index);

            string label = (await row.Locator("a.counter-settled-name").First.InnerTextAsync()).Trim();
            string when = (await row.Locator("span.counter-settled-when").First.InnerTextAsync()).Trim();

            string amountBlock =
                (await row.Locator("div.counter-settled-amount").First.InnerTextAsync()).Trim();

            ILocator corrected = row.Locator("span.counter-settled-corrected");

            string correctedBlock = await corrected.CountAsync() > 0
                ? (await corrected.First.InnerTextAsync()).Trim()
                : string.Empty;

            settled.Add(new SettledTableRow(label, WithoutUnitPrice(amountBlock, correctedBlock), when));
        }

        return new CounterFloor(open, settled);
    }

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

    internal static string DescribeSettled(SettledTill till)
    {
        ArgumentNullException.ThrowIfNull(till);

        string leftovers = till.LineControlCount == 0 && !till.OffersClose && !till.OffersStaffAdd
            ? "no open-sitting controls"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{till.LineControlCount} line control block(s)"
                + $"{(till.OffersClose ? ", a close button" : string.Empty)}"
                + $"{(till.OffersStaffAdd ? ", the staff-add panel" : string.Empty)}");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{till.TotalLabel}' {till.TotalText}, settle panel {till.TableTotalText},"
            + $" read-only note {(till.SaysReadOnly ? "present" : "absent")},"
            + $" correction {(till.ShowsCorrection ? "shown" : "not shown")}, {leftovers};"
            + $" header says '{till.HeaderMeta}'; notice {till.Notice ?? "(none)"}");
    }

    internal static string DescribeFloor(CounterFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);

        string open = floor.OpenTableLabels.Count == 0
            ? "nothing open"
            : string.Join(", ", floor.OpenTableLabels.Select(label => $"'{label}'"));

        string settled = floor.Settled.Count == 0
            ? "nothing settled"
            : string.Join(
                ", ",
                floor.Settled.Select(row => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{row.TableLabel}' {row.AmountText} ({row.SettledBy})")));

        return string.Create(CultureInfo.InvariantCulture, $"open: {open}; settled today: {settled}");
    }

    private static int LeadingCount(string sentence)
    {
        int end = 0;

        while (end < sentence.Length && char.IsAsciiDigit(sentence[end]))
        {
            end++;
        }

        return end > 0
            && int.TryParse(
                sentence[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : 0;
    }

    private static async Task<string> DescribeSettleSectionAsync(IPage page)
    {
        ILocator section = page.Locator("#counter-sitting-surface section.counter-settle");

        if (await section.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"There is no settle panel on the page at all; the browser is at '{page.Url}'.");
        }

        bool offersClose = await page.Locator(CloseButtonSelector).CountAsync() > 0;
        bool confirming = await page.Locator(ConfirmCloseButtonSelector).CountAsync() > 0;
        bool readOnly = await page.Locator(ReadOnlyNoteSelector).CountAsync() > 0;
        string total = (await page.Locator(SettlePanelTotalSelector).First.InnerTextAsync()).Trim();

        string state = (offersClose, confirming, readOnly) switch
        {
            (true, _, _) => "it is offering Close & settle",
            (_, true, _) => "it is holding a confirmation prompt",
            (_, _, true) => "the sitting is already settled",
            _ => "it offers nothing and does not say the sitting is settled, which should be impossible",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The settle panel reads {total} and {state}; the browser is at '{page.Url}'.");
    }

    private static async Task<CounterBillLine> ReadLineAsync(ILocator line)
    {
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

    private static string WithoutUnitPrice(string priceBlock, string unitBlock)
    {
        if (unitBlock.Length == 0)
        {
            return priceBlock;
        }

        int at = priceBlock.LastIndexOf(unitBlock, StringComparison.Ordinal);

        return at < 0 ? priceBlock : priceBlock[..at].Trim();
    }

    private static string WithoutEachSuffix(string unitBlock)
    {
        const string suffix = "each";

        return unitBlock.EndsWith(suffix, StringComparison.Ordinal)
            ? unitBlock[..^suffix.Length].Trim()
            : unitBlock;
    }

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

    private static async Task<CounterBillLine?> FindLineAsync(IPage page, string menuItemName)
    {
        CounterBill bill = await ReadBillAsync(page);

        return bill.People
            .SelectMany(entry => entry.Lines)
            .FirstOrDefault(line => string.Equals(line.Name, menuItemName, StringComparison.Ordinal));
    }

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

    private static async Task<string> DescribeBoardSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(BoardSurfaceAnyStateSelector);

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no counter board on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.First.GetAttributeAsync("data-live");
        string? loaded = await surface.First.GetAttributeAsync("data-loaded");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"data-live='{live ?? "absent"}', data-loaded='{loaded ?? "absent"}'");
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
        string? loaded = await surface.First.GetAttributeAsync("data-loaded");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"data-live='{live ?? "absent"}', data-loaded='{loaded ?? "absent"}'");
    }

    private static string PathFor(Guid sittingIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{SittingPathPrefix}{sittingIdentifier:D}");

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
