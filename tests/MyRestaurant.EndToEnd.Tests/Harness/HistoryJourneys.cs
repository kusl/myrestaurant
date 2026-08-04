using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>One line on a past order, as §11.1's history renders it.</summary>
/// <param name="Quantity">How many, off the "2 ×" prefix — the digits are the data and the multiplication sign is markup.</param>
/// <param name="Name">The menu item's name, as the projection stored it.</param>
/// <param name="Note">§6.5.2's customization note, quoted by the surface, or <c>null</c> when there is none.</param>
/// <param name="LineTotalText">The extension, formatted the way the surface formatted it.</param>
internal sealed record HistoryLine(int Quantity, string Name, string? Note, string LineTotalText);

/// <summary>
/// One settled order on the owner's own history page (§11.1, §6.8).
///
/// <para><paramref name="GuestOrderIdentifier"/> is read off the Hide link's own <c>?hide=</c> query
/// rather than out of the database, on the same reasoning
/// <c>AdministrationJourneys.CreateTableAsync</c> recovers a table's identifier from the "Manage this
/// table" link: the identifier is minted server-side, and a harness that went round the surface to get
/// it would be reimplementing the surface instead of testing it. It is also the identifier §16.3
/// scenario 11 needs in order to say that the row an administrator later finds in the hidden-records
/// view <em>is</em> the order this guest hid, rather than merely that a row appeared.</para>
///
/// <para><paramref name="LineCount"/> is read off the summary row's "3 items" rather than counted from
/// <paramref name="Lines"/>, deliberately. §11.1 renders it from
/// <c>PersonOrderHistoryEntry.LineCount</c>, which is the length of the list the reader projected; the
/// list below is what the markup actually drew. They are the same number until something stops being
/// rendered, and that is precisely the failure worth catching on a page whose whole promise is that a
/// meal is either all there or deliberately hidden.</para>
/// </summary>
internal sealed record HistoryOrder(
    Guid GuestOrderIdentifier,
    string TableLabel,
    string PersonTotalText,
    int LineCount,
    IReadOnlyList<HistoryLine> Lines);

/// <summary>
/// The owner's history page at one instant.
///
/// <para><paramref name="EmptySentence"/> is not the same claim as <c>Orders.Count == 0</c>, and both are
/// worth having. §11.1 draws the sentence only when the reader came back with nothing, so a page
/// carrying neither orders nor the sentence is one whose list failed to render at all — which would
/// satisfy "the hidden order is gone" for entirely the wrong reason.</para>
///
/// <para><paramref name="Notice"/> is §6.8's flash after a hide, which arrives through a
/// post/redirect/get and therefore survives a refresh; <paramref name="Problem"/> is a refusal, which
/// does not redirect. They are mutually exclusive on this page.</para>
/// </summary>
internal sealed record GuestHistory(
    IReadOnlyList<HistoryOrder> Orders,
    string? Notice,
    string? Problem,
    string? EmptySentence);

/// <summary>
/// The guest's own order history and §6.8's Hide (TECHNICAL_SPECIFICATION §6.8, §11.1).
///
/// <para><b>Reached by URL rather than by following a link, and that is a decision rather than a
/// shortcut.</b> Three surfaces link here — the home page, <c>/table</c>, and the settled order surface
/// — but <c>/table/{id}</c> and <c>/table</c> are interactive-server pages while <c>/table/history</c>
/// carries <c>[ExcludeFromInteractiveRouting]</c>, so a link click from either crosses a render-mode
/// boundary. Whatever that does is worth knowing about on its own terms; it is not what §16.3 scenario
/// 11 is about, and a scenario that went red there would be reporting a routing question as a
/// visibility bug. A history page is a bookmarkable thing and this is how somebody returns to one.</para>
///
/// <para><b>The Hide link, on the other hand, does go through
/// <see cref="EnhancedNavigation"/>.</b> This page <em>is</em> static SSR, so
/// <c>blazor.web.js</c> intercepts in-app clicks on it, and the destination is the same route with
/// <c>?hide=</c> set — the confirmation §6.8 requires to \"state plainly that this cannot be undone\".
/// The address bar therefore moves before the confirmation exists, and the barrier is the panel.</para>
///
/// <para><b>Why the confirmation's hidden field is checked.</b> <c>TableHistory.razor</c> renders the
/// panel inside the article it belongs to and posts the order in a hidden input rather than re-reading
/// the query string. A confirmation that opened on the wrong row would look entirely convincing —
/// correct copy, correct buttons — and would hide a meal the guest was not looking at. §6.8 gives this
/// no undo from the guest's account, which makes it the one write in the application where confirming
/// the subject is worth a line of its own.</para>
/// </summary>
internal static class HistoryJourneys
{
    /// <summary>The route. <c>TableHistory.razor</c> is <c>@page "/table/history"</c>.</summary>
    internal const string HistoryPath = "/table/history";

    /// <summary>
    /// The surface, named as of M6 Slice 14. Everything below is scoped through it: the page's flash and
    /// its refusal are <c>p.status-success</c> and <c>p.status-error</c>, which are the same two classes
    /// every other surface in the application uses, and a reader that matched them document-wide would
    /// be reading whatever the layout happened to be saying.
    /// </summary>
    private const string SurfaceSelector = "#table-history-surface";

    private const string OrderSelector = SurfaceSelector + " article.history-order";
    private const string HideLinkSelector = "a.history-hide-link";
    private const string ConfirmSelector = SurfaceSelector + " div.history-confirm";
    private const string ConfirmSubmitSelector = ConfirmSelector + " button[type='submit']";
    private const string ConfirmOrderFieldSelector = ConfirmSelector + " input[name='order']";
    private const string NoticeSelector = SurfaceSelector + " p.status-success";
    private const string ProblemSelector = SurfaceSelector + " p.status-error";

    /// <summary>
    /// §11.1's \"nothing here yet\" sentence, given a class of its own as of M6 Slice 14 — it was
    /// otherwise a <c>p.lede</c> among the page's other <c>p.lede</c>s and could only be reached by
    /// position.
    /// </summary>
    private const string EmptySelector = SurfaceSelector + " p.history-none";

    /// <summary>What <c>TableHistory.HidePath</c> puts in front of the order identifier.</summary>
    private const string HideQuery = "?hide=";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Opens the history page and returns once it is on screen.
    ///
    /// <para>Static SSR, so <c>OnInitializedAsync</c> has already run server-side and the list is in the
    /// response — there is nothing to settle after the navigation. The wait is here for what it says
    /// when it fails: a §3.7 policy refusal renders the access-denied panel and an expired cookie
    /// redirects to sign-in, and both of those look exactly like an empty history to a reader that
    /// went straight to counting articles.</para>
    /// </summary>
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

    /// <summary>
    /// The page as it stands right now.
    ///
    /// <para><b>Do not call this while a confirmation is open.</b> §11.1 replaces a row's Hide link with
    /// the confirmation panel, and the link is where the order identifier comes from — so a row without
    /// one is unreadable here and says so rather than reporting <c>Guid.Empty</c>. Reading a list
    /// mid-confirmation is not something a scenario has a reason to do, and quietly answering it with a
    /// sentinel is how an assertion ends up passing against nothing.</para>
    /// </summary>
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

    /// <summary>
    /// §6.8's hide, all the way through: opens the confirmation for one order, checks it is about that
    /// order, accepts it, and returns once the page has said so.
    ///
    /// <para><b>The refusal is looked for first.</b> §6.8's three refusals — the order is gone, it is not
    /// yours, its sitting is still open — leave the list exactly as it was and write a sentence, while a
    /// hide that landed redirects and writes a different one. They are mutually exclusive, so the order
    /// of the two checks does not change the outcome; looking at the refusal first means a scenario that
    /// hits one is told what the surface said rather than being told the flash never arrived.</para>
    /// </summary>
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

    /// <summary>A short, quotable rendering of a history page, for a failure message.</summary>
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

    /// <summary>A short, quotable rendering of a past order's lines, for a failure message.</summary>
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

    // --- internals ---------------------------------------------------------------------------------

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
        // "2 ×" — the multiplication sign is markup rather than data, the same treatment
        // CounterJourneys and KitchenJourneys give their own quantities.
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

    /// <summary>
    /// The declared text of the first match, or <c>null</c> when the element is not on the page. Used for
    /// the three paragraphs whose <em>absence</em> is as much a fact as their contents.
    /// </summary>
    private static async Task<string?> TextIfPresentAsync(IPage page, string selector)
    {
        ILocator located = page.Locator(selector);

        return await located.CountAsync() > 0
            ? await ScreenText.DeclaredAsync(located.First)
            : null;
    }

    /// <summary>
    /// The leading integer of "3 items", or <c>0</c> when the copy does not start with one. Zero is
    /// unambiguous rather than a plausible value: §11.1 draws the summary row only for an order it is
    /// also drawing, and an order with no lines renders "0 items" — so a zero from a row that says
    /// something else means the copy changed shape.
    /// </summary>
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
