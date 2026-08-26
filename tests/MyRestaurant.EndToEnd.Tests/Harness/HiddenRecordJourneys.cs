using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record HiddenRecordRow(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    string OwnerName,
    string Username,
    string PersonTotalText);

internal sealed record HiddenVisibilityEntry(string Description, string ActorAndTime);

internal sealed record HiddenRecordDetail(
    IReadOnlyList<HiddenVisibilityEntry> VisibilityLog,
    int EventCount,
    bool OffersUnhide);

internal sealed record HiddenRecordList(
    IReadOnlyList<HiddenRecordRow> Rows,
    string? CountSentence,
    string? EmptySentence,
    bool IsNarrowed,
    string? Notice,
    string? Problem);

internal static class HiddenRecordJourneys
{
    internal const string HiddenRecordsPath = "/administration/hidden-records";

    private const string SurfaceSelector = "#hidden-records-surface";

    private const string RowSelector = SurfaceSelector + " article.hidden-record";
    private const string ExpandLinkSelector = "a:has-text('Open the complete record')";
    private const string SittingLinkSelector = "a:has-text('The whole sitting')";

    private const string FilterUsernameSelector = SurfaceSelector + " form.filter-form #filter-username";
    private const string FilterSubmitSelector =
        SurfaceSelector + " form.filter-form button[type='submit']";

    private const string ShowEverythingSelector =
        SurfaceSelector + " form.filter-form .filter-actions a";

    private const string CountSelector = SurfaceSelector + " p.filter-count";

    private const string EmptySelector = SurfaceSelector + " p.hidden-none";

    private const string UnhidePanelSelector = SurfaceSelector + " div.hidden-unhide";
    private const string UnhideSubmitSelector = UnhidePanelSelector + " button[type='submit']";

    private const string VisibilityEventSelector = SurfaceSelector + " ol.hidden-events > li";
    private const string RecordEventHeadSelector = "div.hidden-event-head";

    private const string NoticeSelector = SurfaceSelector + " p.status-success";
    private const string ProblemSelector = SurfaceSelector + " p.status-error";

    private const string RecordQuery = "record=";
    private const string SittingPathPrefix = "/administration/sittings/";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task OpenAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(HiddenRecordsPath);
        await WaitForSurfaceAsync(page, timeout);
    }

    internal static async Task FilterByUsernameAsync(IPage page, string username, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.FillAsync(FilterUsernameSelector, username);

        string before = page.Url;

        await page.Locator(FilterSubmitSelector).First.ClickAsync();

        try
        {
            await page.WaitForURLAsync(
                url => !string.Equals(url, before, StringComparison.Ordinal),
                new PageWaitForURLOptions { Timeout = (float)timeout.TotalMilliseconds });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Narrowing the hidden-records list to '{username}' never left '{before}' within"
                    + $" {timeout.TotalSeconds:F0}s. §11.4's filter is a plain GET form with no"
                    + $" data-enhance, so this is an ordinary navigation and the URL must carry the"
                    + $" username — unless the previous filter was already the same one, in which case"
                    + $" there is no navigation to wait for and nothing to learn from it."),
                exception);
        }

        await WaitForSurfaceAsync(page, timeout);

        string? applied = await page.Locator(FilterUsernameSelector).First.InputValueAsync();

        if (!string.Equals(applied?.Trim(), username, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The hidden-records filter came back holding '{applied}' rather than '{username}',"
                    + $" so the list on screen is answering a different question. The browser is at"
                    + $" '{page.Url}'."));
        }
    }

    internal static async Task<HiddenRecordList> ReadAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator rows = page.Locator(RowSelector);
        int count = await rows.CountAsync();

        List<HiddenRecordRow> read = new(count);

        for (int index = 0; index < count; index++)
        {
            read.Add(await ReadRowAsync(page, rows.Nth(index), index));
        }

        return new HiddenRecordList(
            read,
            await TextIfPresentAsync(page, CountSelector),
            await TextIfPresentAsync(page, EmptySelector),
            await page.Locator(ShowEverythingSelector).CountAsync() > 0,
            await TextIfPresentAsync(page, NoticeSelector),
            await TextIfPresentAsync(page, ProblemSelector));
    }

    internal static async Task<HiddenRecordDetail> ExpandAsync(
        IPage page,
        Guid guestOrderIdentifier,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator expandLink = await LocateExpandLinkAsync(page, guestOrderIdentifier);

        await EnhancedNavigation.FollowAsync(
            page,
            expandLink,
            UnhidePanelSelector,
            "§11.4's complete stored record with its per-record Unhide",
            timeout);

        return await ReadDetailAsync(page);
    }

    internal static async Task UnhideAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator submit = page.Locator(UnhideSubmitSelector);

        try
        {
            await submit.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no Unhide to press. §11.4 renders it only inside an expanded record, so"
                    + $" ExpandAsync either was not called or the row has since left the list. The"
                    + $" browser is at '{page.Url}'."),
                exception);
        }

        await submit.First.ClickAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await TextIfPresentAsync(page, ProblemSelector) is { } refusal)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The unhide was refused, so no unhidden row was appended. §11.4 says:"
                        + $" {refusal}"));
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
                $"The unhide neither succeeded nor was refused within {timeout.TotalSeconds:F0}s. It is"
                + $" an ordinary form post that redirects on both Unhidden and NotHidden, so a missing"
                + $" flash means the post never left. The browser is at '{page.Url}'."));
    }

    internal static string Describe(HiddenRecordList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        string rows = list.Rows.Count == 0
            ? "no rows"
            : string.Join(
                " | ",
                list.Rows.Select(row => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{row.Username}' ({row.OwnerName}) {row.PersonTotalText}"
                    + $" order {row.GuestOrderIdentifier:D}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{rows}; {(list.IsNarrowed ? "narrowed" : "unfiltered")};"
            + $" count sentence {list.CountSentence ?? "(none)"};"
            + $" empty sentence {list.EmptySentence ?? "(none)"};"
            + $" notice {list.Notice ?? "(none)"}; problem {list.Problem ?? "(none)"}");
    }

    internal static string DescribeVisibilityLog(IReadOnlyList<HiddenVisibilityEntry> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        return log.Count == 0
            ? "no visibility events at all"
            : string.Join(
                "; ",
                log.Select(entry => string.Create(
                    CultureInfo.InvariantCulture, $"'{entry.Description}' ({entry.ActorAndTime})")));
    }

    private static async Task WaitForSurfaceAsync(IPage page, TimeSpan timeout)
    {
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
                    $"{HiddenRecordsPath} never rendered its own surface within"
                    + $" {timeout.TotalSeconds:F0}s. §3.7 admits administrators only, so a principal"
                    + $" that failed the policy is looking at the access-denied panel; the browser is at"
                    + $" '{page.Url}'."),
                exception);
        }
    }

    private static async Task<HiddenRecordDetail> ReadDetailAsync(IPage page)
    {
        ILocator entries = page.Locator(VisibilityEventSelector);
        int count = await entries.CountAsync();

        List<HiddenVisibilityEntry> visibility = [];
        int events = 0;

        for (int index = 0; index < count; index++)
        {
            ILocator entry = entries.Nth(index);

            if (await entry.Locator(RecordEventHeadSelector).CountAsync() > 0)
            {
                events++;
                continue;
            }

            visibility.Add(new HiddenVisibilityEntry(
                await ScreenText.DeclaredAsync(entry.Locator("span.hidden-event-type").First),
                await ScreenText.DeclaredAsync(entry.Locator("span.hidden-muted-inline").First)));
        }

        return new HiddenRecordDetail(
            visibility,
            events,
            await page.Locator(UnhideSubmitSelector).CountAsync() > 0);
    }

    private static async Task<HiddenRecordRow> ReadRowAsync(IPage page, ILocator row, int index)
    {
        Guid guestOrderIdentifier = await OrderIdentifierFromExpandLinkAsync(page, row, index);

        string? sittingHref = await row.Locator(SittingLinkSelector).First.GetAttributeAsync("href");
        int at = sittingHref?.IndexOf(SittingPathPrefix, StringComparison.Ordinal) ?? -1;

        if (sittingHref is null
            || at < 0
            || !Guid.TryParse(sittingHref[(at + SittingPathPrefix.Length)..], out Guid sittingIdentifier))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The row at position {index} links its sitting as '{sittingHref}', which is not a"
                    + $" sitting record URL."));
        }

        return new HiddenRecordRow(
            guestOrderIdentifier,
            sittingIdentifier,
            (await row.Locator("h2.hidden-record-title").First.InnerTextAsync()).Trim(),
            (await row.Locator("span.hidden-username").First.InnerTextAsync()).Trim(),
            (await row.Locator("span.hidden-amount").First.InnerTextAsync()).Trim());
    }

    private static async Task<Guid> OrderIdentifierFromExpandLinkAsync(
        IPage page,
        ILocator row,
        int index)
    {
        ILocator expandLink = row.Locator(ExpandLinkSelector);

        if (await expandLink.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The row at position {index} offers no \"Open the complete record\" link, so its"
                    + $" order cannot be named. §11.4 replaces it with \"Close the record\" on exactly"
                    + $" one row — the expanded one — and that link carries no identifier, so this list"
                    + $" is being read while a record is open. Read it before expanding anything. The"
                    + $" browser is at '{page.Url}'."));
        }

        string? href = await expandLink.First.GetAttributeAsync("href");
        int at = href?.IndexOf(RecordQuery, StringComparison.Ordinal) ?? -1;

        if (href is null
            || at < 0
            || !Guid.TryParse(href[(at + RecordQuery.Length)..], out Guid guestOrderIdentifier))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The expand link on the row at position {index} points at '{href}', which does not"
                    + $" name an order. §11.4 builds it from the current filter plus record=, so a"
                    + $" filter value containing an ampersand would land here."));
        }

        return guestOrderIdentifier;
    }

    private static async Task<ILocator> LocateExpandLinkAsync(IPage page, Guid guestOrderIdentifier)
    {
        ILocator rows = page.Locator(RowSelector);
        int count = await rows.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator row = rows.Nth(index);
            ILocator expandLink = row.Locator(ExpandLinkSelector);

            if (await expandLink.CountAsync() == 0)
            {
                continue;
            }

            string? href = await expandLink.First.GetAttributeAsync("href");

            if (href is not null
                && href.EndsWith(
                    string.Create(
                        CultureInfo.InvariantCulture, $"{RecordQuery}{guestOrderIdentifier:D}"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return expandLink.First;
            }
        }

        string list = Describe(await ReadAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no hidden order {guestOrderIdentifier:D} in the list to expand. §11.4 lists"
                + $" every currently-hidden order system-wide, narrowed only by the filter on screen —"
                + $" so it has either been unhidden or the filter excludes it. The list holds: {list}."));
    }

    private static async Task<string?> TextIfPresentAsync(IPage page, string selector)
    {
        ILocator located = page.Locator(selector);

        return await located.CountAsync() > 0
            ? await ScreenText.DeclaredAsync(located.First)
            : null;
    }
}
