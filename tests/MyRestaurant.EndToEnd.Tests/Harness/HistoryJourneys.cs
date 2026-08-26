using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record HistoryLine(int Quantity, string Name, string? Note, string LineTotalText);

internal sealed record HistoryOrder(
    Guid GuestOrderIdentifier,
    string TableLabel,
    string PersonTotalText,
    int LineCount,
    IReadOnlyList<HistoryLine> Lines);

internal sealed record GuestHistory(
    IReadOnlyList<HistoryOrder> Orders,
    string? Notice,
    string? Problem,
    string? EmptySentence);

internal static class HistoryJourneys
{
    internal const string HistoryPath = "/table/history";

    private const string SurfaceSelector = "#table-history-surface";

    private const string OrderSelector = SurfaceSelector + " article.history-order";
    private const string HideLinkSelector = "a.history-hide-link";
    private const string ConfirmSelector = SurfaceSelector + " div.history-confirm";
    private const string ConfirmSubmitSelector = ConfirmSelector + " button[type='submit']";
    private const string ConfirmOrderFieldSelector = ConfirmSelector + " input[name='order']";
    private const string NoticeSelector = SurfaceSelector + " p.status-success";
    private const string ProblemSelector = SurfaceSelector + " p.status-error";

    private const string EmptySelector = SurfaceSelector + " p.history-none";

    private const string HideQuery = "?hide=";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task OpenAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(HistoryPath);

        try
        {
            await page.Locator(SurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
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
                    $"{HistoryPath} never rendered its own surface within {timeout.TotalSeconds:F0}s."
                    + $" §11.1 puts the history behind the table policy, so a principal that failed it"
                    + $" is looking at the access-denied panel and an expired cookie is at the sign-in"
                    + $" page; the browser is at '{page.Url}'."),
                exception);
        }
    }

    internal static async Task<GuestHistory> ReadAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator orders = page.Locator(OrderSelector);
        int count = await orders.CountAsync();

        List<HistoryOrder> read = new(count);

        for (int index = 0; index < count; index++)
        {
            read.Add(await ReadOrderAsync(page, orders.Nth(index), index));
        }

        return new GuestHistory(
            read,
            await TextIfPresentAsync(page, NoticeSelector),
            await TextIfPresentAsync(page, ProblemSelector),
            await TextIfPresentAsync(page, EmptySelector));
    }

    internal static async Task HideAsync(IPage page, Guid guestOrderIdentifier, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator hideLink = await LocateHideLinkAsync(page, guestOrderIdentifier);

        await EnhancedNavigation.FollowAsync(
            page,
            hideLink,
            ConfirmSelector,
            "§6.8's confirmation that a hide cannot be undone from the guest's account",
            timeout);

        string? posting = await page.Locator(ConfirmOrderFieldSelector).First.GetAttributeAsync("value");

        if (!Guid.TryParse(posting, out Guid confirming) || confirming != guestOrderIdentifier)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The confirmation on screen would post order '{posting}' rather than"
                    + $" {guestOrderIdentifier:D}. §6.8 gives a guest no undo, so hiding the wrong meal"
                    + $" is not a recoverable mistake — this is refused rather than submitted."));
        }

        await page.Locator(ConfirmSubmitSelector).First.ClickAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await TextIfPresentAsync(page, ProblemSelector) is { } refusal)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Hiding {guestOrderIdentifier:D} was refused, so nothing was written. §11.1"
                        + $" says: {refusal}"));
            }

            if (await TextIfPresentAsync(page, NoticeSelector) is not null)
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Hiding {guestOrderIdentifier:D} neither succeeded nor was refused within"
                + $" {timeout.TotalSeconds:F0}s. The confirmation is an ordinary form post — no"
                + $" data-enhance, so no enhanced navigation — and it redirects on both Hidden and"
                + $" AlreadyHidden, so a missing flash means the post never left. The browser is at"
                + $" '{page.Url}'."));
    }

    internal static string Describe(GuestHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        string orders = history.Orders.Count == 0
            ? "no orders"
            : string.Join(
                " | ",
                history.Orders.Select(order => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{order.TableLabel}' {order.PersonTotalText} ({order.LineCount} line(s):"
                    + $" {DescribeLines(order.Lines)})")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{orders}; empty sentence {history.EmptySentence ?? "(none)"};"
            + $" notice {history.Notice ?? "(none)"}; problem {history.Problem ?? "(none)"}");
    }

    internal static string DescribeLines(IReadOnlyList<HistoryLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing was left on it"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{line.Quantity} × '{line.Name}' {line.LineTotalText}"
                    + $"{(line.Note is null ? string.Empty : $" — “{line.Note}”")}")));
    }

    private static async Task<HistoryOrder> ReadOrderAsync(IPage page, ILocator order, int index)
    {
        ILocator hideLink = order.Locator(HideLinkSelector);

        if (await hideLink.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The order at position {index} offers no Hide link, so its identifier cannot be"
                    + $" read. §11.1 withholds the link from exactly one row — the one whose"
                    + $" confirmation panel is open — so this page is mid-confirmation and reading its"
                    + $" list is not a meaningful thing to do. The browser is at '{page.Url}'."));
        }

        string? href = await hideLink.First.GetAttributeAsync("href");
        int at = href?.IndexOf(HideQuery, StringComparison.Ordinal) ?? -1;

        if (href is null
            || at < 0
            || !Guid.TryParse(href[(at + HideQuery.Length)..], out Guid guestOrderIdentifier))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The Hide link on the order at position {index} points at '{href}', which does"
                    + $" not name an order."));
        }

        string countText = await ScreenText.DeclaredAsync(
            order.Locator("span.history-order-count").First);

        ILocator lines = order.Locator("ul.history-lines li");
        int lineCount = await lines.CountAsync();

        List<HistoryLine> read = new(lineCount);

        for (int index2 = 0; index2 < lineCount; index2++)
        {
            read.Add(await ReadLineAsync(lines.Nth(index2)));
        }

        return new HistoryOrder(
            guestOrderIdentifier,
            (await order.Locator("h2.history-order-title").First.InnerTextAsync()).Trim(),
            (await order.Locator("span.history-amount").First.InnerTextAsync()).Trim(),
            LeadingCount(countText),
            read);
    }

    private static async Task<HistoryLine> ReadLineAsync(ILocator line)
    {
        string quantityText = (await line.Locator("span.history-line-quantity").First.InnerTextAsync())
            .Trim()
            .TrimEnd('×')
            .Trim();

        ILocator note = line.Locator("span.history-line-note");

        return new HistoryLine(
            int.TryParse(
                quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity)
                ? quantity
                : 0,
            (await line.Locator("span.history-line-item").First.InnerTextAsync()).Trim(),
            await note.CountAsync() > 0 ? (await note.First.InnerTextAsync()).Trim() : null,
            (await line.Locator("span.history-line-amount").First.InnerTextAsync()).Trim());
    }

    private static async Task<ILocator> LocateHideLinkAsync(IPage page, Guid guestOrderIdentifier)
    {
        ILocator orders = page.Locator(OrderSelector);
        int count = await orders.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator hideLink = orders.Nth(index).Locator(HideLinkSelector);

            if (await hideLink.CountAsync() == 0)
            {
                continue;
            }

            string? href = await hideLink.First.GetAttributeAsync("href");

            if (href is not null
                && href.EndsWith(
                    string.Create(CultureInfo.InvariantCulture, $"{HideQuery}{guestOrderIdentifier:D}"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return hideLink.First;
            }
        }

        string history = Describe(await ReadAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no order {guestOrderIdentifier:D} on this history page to hide. §11.1 lists"
                + $" only the owner's own settled, not-already-hidden orders, so it is absent for one of"
                + $" those three reasons. The page holds: {history}."));
    }

    private static async Task<string?> TextIfPresentAsync(IPage page, string selector)
    {
        ILocator located = page.Locator(selector);

        return await located.CountAsync() > 0
            ? await ScreenText.DeclaredAsync(located.First)
            : null;
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
}
