using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One row of §11.4's hidden-records list.
///
/// <para>Both identifiers come off the row's own two links — <c>?record=</c> on "Open the complete
/// record" and <c>/administration/sittings/{id}</c> on "The whole sitting" — rather than out of the
/// database or out of a <c>data-</c> attribute added for the harness. That is the same recovery
/// <c>AdministrationJourneys</c> already does from a "Manage this…" link, and it makes the row's identity
/// a thing the surface asserts rather than a thing the test supplies.</para>
///
/// <para><b>The table label and the line count are deliberately absent.</b> §11.4 renders both inside
/// paragraphs that carry other metadata under the same class — the label sits between a username and a
/// timestamp in one sentence, and the line count is the second of two <c>span.hidden-record-note</c>s —
/// so reaching either means splitting prose or indexing by position. Both facts are asserted where they
/// have elements of their own: the label on the guest's own history page, the line count there too. A
/// harness field that could only be filled by counting siblings is a field that starts lying the day a
/// third note is added.</para>
/// </summary>
/// <param name="GuestOrderIdentifier">The hidden order, off the expand link.</param>
/// <param name="SittingIdentifier">The sitting it belongs to, off the sitting link.</param>
/// <param name="OwnerName">The row's heading — the display name, or the username when none is set.</param>
/// <param name="Username">The thing §6.8's filter matches on, rendered in its own element.</param>
/// <param name="PersonTotalText">The owner's own share, formatted the way the surface formatted it.</param>
internal sealed record HiddenRecordRow(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    string OwnerName,
    string Username,
    string PersonTotalText);

/// <summary>
/// One entry of an order's visibility log, as §11.4's expanded record renders it.
///
/// <para>Two fields because the surface gives them two elements, and because they answer two questions
/// §6.8 keeps separate: <paramref name="Description"/> is which of the two stored event types this is,
/// and <paramref name="ActorAndTime"/> names who did it. §6.8 gives the hide to the owner and the unhide
/// to an administrator, and the stored word does not distinguish them — the prose calls the
/// administrator's row <c>unhidden_by_administrator</c> while the column holds <c>unhidden</c> — so the
/// actor beside it is the only place the distinction lives.</para>
/// </summary>
internal sealed record HiddenVisibilityEntry(string Description, string ActorAndTime);

/// <summary>
/// One order's complete stored record, as far as §16.3 scenario 11 judges it.
///
/// <para><paramref name="EventCount"/> is a count rather than a list because §11.4's requirement here is
/// completeness — "full event log … unprojected" — and a scenario that enumerated the log would be
/// re-asserting §6.5's vocabulary, which the data-access tests own. What this can say that nothing else
/// can is that hiding an order did not take its history with it.</para>
/// </summary>
internal sealed record HiddenRecordDetail(
    IReadOnlyList<HiddenVisibilityEntry> VisibilityLog,
    int EventCount,
    bool OffersUnhide);

/// <summary>
/// The hidden-records view at one instant.
///
/// <para><paramref name="IsNarrowed"/> is read from the surface rather than tracked by the caller.
/// §11.4's rule is that administration starts complete and narrows "only on explicit request", and the
/// page publishes which of those it is in by offering a "Show everything" escape or not — so a filter
/// that silently failed to apply, or one that stuck when it should have cleared, is visible here rather
/// than inferred from a row count that would look identical either way.</para>
///
/// <para><paramref name="EmptySentence"/> carries whichever of §11.4's two empty sentences is on screen,
/// and the difference between them is the point. "Nothing hidden matches that" says the filter excluded
/// everything; "Nothing is hidden anywhere in the restaurant" is the stronger claim, and is the one an
/// unhide has to produce.</para>
/// </summary>
internal sealed record HiddenRecordList(
    IReadOnlyList<HiddenRecordRow> Rows,
    string? CountSentence,
    string? EmptySentence,
    bool IsNarrowed,
    string? Notice,
    string? Problem);

/// <summary>
/// Administration's hidden-records view (TECHNICAL_SPECIFICATION §6.8, §11.4) — the only unhide path in
/// the application.
///
/// <para><b>Three different kinds of navigation live on this one page, and each needs its own barrier.</b>
/// The list is reached by URL. The filter is a plain <c>method="get"</c> form with no
/// <c>data-enhance</c>, so submitting it is an ordinary browser navigation. The expand link is an in-app
/// link on a static-SSR page, so <c>blazor.web.js</c> intercepts it and the address bar moves before the
/// document does — see <see cref="EnhancedNavigation"/>. The Unhide is an ordinary form post that
/// redirects. Getting any of the four wrong produces a read of the previous page, which on a screen whose
/// subject is <em>absence</em> is the failure that most reliably passes.</para>
///
/// <para><b>Why the filter waits on the URL changing rather than on anything in the markup.</b> The
/// obvious barrier is the "Show everything" link, which §11.4 renders exactly when the filter is
/// narrowed — and it is a perfect barrier for the first filter and no barrier at all for the second,
/// because it is already there. The list itself cannot serve either: a filter that correctly matches
/// nothing leaves a page whose rows are unchanged from a filter that has not applied yet, which is
/// precisely the assertion §16.3 scenario 11 makes. The URL is the one thing that always moves, and for
/// an ordinary navigation it moves when the new document commits rather than before it. The single
/// precondition is that two consecutive filters differ; a caller that filtered twice by the same
/// username would be asking a question it already had the answer to.</para>
/// </summary>
internal static class HiddenRecordJourneys
{
    /// <summary>The route. <c>HiddenRecords.razor</c> is <c>@page "/administration/hidden-records"</c>.</summary>
    internal const string HiddenRecordsPath = "/administration/hidden-records";

    /// <summary>The surface, named as of M6 Slice 14 — the scoping root for the two status paragraphs.</summary>
    private const string SurfaceSelector = "#hidden-records-surface";

    private const string RowSelector = SurfaceSelector + " article.hidden-record";
    private const string ExpandLinkSelector = "a:has-text('Open the complete record')";
    private const string SittingLinkSelector = "a:has-text('The whole sitting')";

    private const string FilterUsernameSelector = SurfaceSelector + " form.hidden-filter #filter-username";
    private const string FilterSubmitSelector =
        SurfaceSelector + " form.hidden-filter button[type='submit']";

    /// <summary>
    /// §11.4's escape from a narrowed list. Rendered from <c>_filter.IsNarrowed</c> and from nothing else,
    /// which makes its presence the surface's own answer to "is this list filtered".
    /// </summary>
    private const string ShowEverythingSelector =
        SurfaceSelector + " form.hidden-filter .hidden-filter-actions a";

    private const string CountSelector = SurfaceSelector + " p.hidden-count";

    /// <summary>
    /// §11.4's empty sentence, given a class of its own as of M6 Slice 14. It carries two different
    /// sentences through a ternary and was otherwise one <c>p.lede</c> among several.
    /// </summary>
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

    /// <summary>Opens the view unfiltered — the state §11.4 says it starts in.</summary>
    internal static async Task OpenAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(HiddenRecordsPath);
        await WaitForSurfaceAsync(page, timeout);
    }

    /// <summary>
    /// §6.8's username filter, typed into the form and submitted — not appended to the URL.
    ///
    /// <para>The distinction matters because §16.3 words scenario 11 as an administrator who
    /// <em>filters</em>, and a query string assembled by the harness would exercise
    /// <c>[SupplyParameterFromQuery]</c> while skipping the form, the labels, and the round trip. The
    /// filter being a GET is itself part of §11.4: a filter is a bookmarkable question rather than a
    /// mutation, which is why there is no antiforgery token on it and no handler behind it.</para>
    /// </summary>
    internal static async Task FilterByUsernameAsync(IPage page, string username, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.FillAsync(FilterUsernameSelector, username);

        // Captured before the click, because that is the whole barrier: an ordinary form GET commits its
        // new document and only then moves the address bar, so a URL that differs from this one is a page
        // that has arrived.
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

        // The rendered field, not the one that was typed into: the value comes back from the query string
        // through [SupplyParameterFromQuery], so this is the server agreeing about what it was asked.
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

    /// <summary>The list as it stands right now.</summary>
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

    /// <summary>
    /// Opens one row's complete record (§11.4) and returns it.
    ///
    /// <para>The barrier is the Unhide panel rather than one of the three section headings, and for a
    /// reason worth stating: the headings are drawn whether or not the reads behind them came back with
    /// anything, so waiting on one would return from a record that had rendered its own failure prose.
    /// The Unhide panel is the last thing in the expanded article and is drawn from the same
    /// <c>IsExpanded</c> branch, so its arrival means the whole record is on screen.</para>
    /// </summary>
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

    /// <summary>
    /// §6.8's per-record Unhide, on a row that is already expanded, returning once §11.4 has said what
    /// happened.
    ///
    /// <para>Both of the outcomes that mean "this order is visible again" redirect and flash —
    /// <c>Unhidden</c> because this administrator did it, <c>NotHidden</c> because another one got there
    /// first, which §6.8 treats as the system working rather than as a failure. Only a vanished order
    /// writes a problem, and that is what this refuses on. The distinction between the first two is in the
    /// notice, and a caller that cares has it.</para>
    /// </summary>
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

    /// <summary>A short, quotable rendering of the list, for a failure message.</summary>
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

    /// <summary>A short, quotable rendering of a visibility log, for a failure message.</summary>
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

    // --- internals ---------------------------------------------------------------------------------

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

    /// <summary>
    /// The two logs an expanded record draws, told apart by what their entries contain rather than by
    /// where they sit.
    ///
    /// <para>§11.4 renders both as <c>ol.hidden-events</c>, and the obvious way to separate them is by
    /// position or by the heading above — the visibility log's list is the heading's next sibling and the
    /// event log's is not, which is true today and is exactly the kind of thing that stops being true
    /// when a paragraph is added. The stable difference is structural: a stored event wraps its metadata
    /// in <c>div.hidden-event-head</c> because it has a sequence number to put beside the type, and a
    /// visibility event has no such wrapper. So every <c>li</c> is walked once and sorted by whether it
    /// contains that element.</para>
    /// </summary>
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
