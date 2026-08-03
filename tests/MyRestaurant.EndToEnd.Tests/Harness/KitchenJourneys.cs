using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>One line waiting on the pass, as §11.2 renders it.</summary>
internal sealed record KitchenBoardLine(int Quantity, string Name, string? Note);

/// <summary>
/// Everything the board is saying at one instant: how many alerts nobody has acknowledged (§10.3), and
/// what is outstanding (§11.2).
///
/// <para>Read together on purpose. §9 publishes <c>OrderLinesChanged</c> before <c>KitchenAlert</c>, and
/// the board handles each as it arrives — so there is a real window in which the queue has already
/// re-read and the alert has not yet been counted. A scenario that waited for the lines and then read
/// the count separately would sample that window and report a silent kitchen. One snapshot, one
/// predicate over both, and the window closes.</para>
/// </summary>
internal sealed record KitchenBoardSnapshot(int UnseenAlertCount, IReadOnlyList<KitchenBoardLine> PendingLines);

/// <summary>
/// The kitchen journeys the §16.3 scenarios walk: opening the board, watching what arrives on it,
/// marking a line away, and turning a menu item off (TECHNICAL_SPECIFICATION §7, §10, §11.2).
///
/// <para><b>The board must be live before anything is sent to it.</b> Alerts are §9 broadcasts to
/// subscribers, and <c>KitchenBoard.razor</c> subscribes in <c>OnAfterRender(firstRender)</c> — which
/// only runs on a circuit. A board opened after the send would show the queue perfectly well (that comes
/// from <c>kitchen_pending_line</c>) and would never have heard the alert, which is the half of §10 that
/// cannot be re-derived from the database. So <see cref="OpenAsync"/> waits for interactivity, and a
/// scenario opens the board before the guest presses Send.</para>
///
/// <para><b>Sound is not armed and not asserted.</b> §10.3's arm control must run inside a real user
/// gesture to unlock browser audio, and what it unlocks is an <c>AudioContext</c> on a headless browser
/// with no output device — so "did it beep" is a question about Chromium's audio stack rather than about
/// this application. §10.3 names the visual badge as the fallback whenever sound is not working and
/// makes the unseen count the record of what arrived; that count is what these scenarios assert on, and
/// it is the same number the sound is played from.</para>
///
/// <para><b>Why the "86" panel lives here rather than in <see cref="AdministrationJourneys"/>.</b> §7's
/// availability flip has two surfaces — <c>/administration/menu/{id}</c>'s form post and §11.2's live
/// toggle — and they are not interchangeable for a scenario. The administration one is static SSR, so
/// reaching it means navigating a page away from whatever it was showing; the kitchen's is an
/// <c>@onclick</c> on a board a scenario already has open and already needs to keep watching. Both go
/// through <c>IMenuWorkflow</c> to the same write and the same <c>MenuChanged</c> broadcast (§9), so
/// nothing is given up by using the one that does not move the browser.</para>
/// </summary>
internal static class KitchenJourneys
{
    /// <summary>The board's route. <c>KitchenBoard.razor</c> is <c>@page "/kitchen"</c>.</summary>
    internal const string BoardPath = "/kitchen";

    private const string SurfaceSelector = "#kitchen-board-surface";

    /// <summary>
    /// The board as rendered by a live circuit. <c>KitchenBoard.razor</c> sets <c>data-live</c> from
    /// <c>RendererInfo.IsInteractive</c>, so this matches only markup an interactive renderer produced.
    /// </summary>
    private const string LiveSurfaceSelector = "#kitchen-board-surface[data-live='true']";

    private const string PendingLineSelector = "#kitchen-board-surface li.kitchen-line";

    /// <summary>§11.2's "86" panel — one row per menu item, with the toggle that turns it off and on.</summary>
    private const string MenuItemSelector = "#kitchen-board-surface ul.kitchen-menu > li.kitchen-menu-item";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long an availability flip has to reach the panel it was pressed on. One row update against a
    /// local PostgreSQL, so this is only ever reached when the write was refused or the circuit went
    /// away — the same thirty seconds every other page operation in this harness gets.
    /// </summary>
    private static readonly TimeSpan AvailabilityPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens the board on a page already signed in as somebody §3.7 lets in (kitchen or administrator)
    /// and returns once a circuit is behind it.
    /// </summary>
    internal static async Task OpenAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(BoardPath);
        await WaitForLiveBoardAsync(page, timeout);
    }

    /// <summary>
    /// Waits until the board on screen was rendered by a live circuit rather than by prerendering.
    ///
    /// <para>A prerendered board is the worst kind of broken, because it is the kind that looks right: it
    /// lists whatever was outstanding at the moment of the request, in the right order, with the right
    /// waiting times — and then never changes and never makes a sound. A kitchen that has had no orders
    /// for ten minutes looks exactly the same. Waiting here means a scenario says "no circuit" rather
    /// than "the alert never arrived".</para>
    /// </summary>
    internal static async Task WaitForLiveBoardAsync(IPage page, TimeSpan timeout)
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
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The kitchen board never became interactive within {timeout.TotalSeconds:F0}s;"
                    + $" it is still the prerendered markup ({surface}). It will list whatever was"
                    + $" outstanding when the page was requested and then never change and never alert,"
                    + $" because §9's broadcasts go to subscribers and the subscription is made on the"
                    + $" circuit. Check that /_framework/blazor.web.js is served and that the browser"
                    + $" reached /_blazor."),
                exception);
        }
    }

    /// <summary>What the board is saying right now — the unseen-alert count and the outstanding lines.</summary>
    internal static async Task<KitchenBoardSnapshot> ReadBoardAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator surface = page.Locator(SurfaceSelector).First;
        string? unseen = await surface.GetAttributeAsync("data-unseen-alerts");

        if (!int.TryParse(unseen, NumberStyles.Integer, CultureInfo.InvariantCulture, out int unseenCount))
        {
            throw new InvalidOperationException(
                $"The kitchen board published data-unseen-alerts='{unseen ?? "absent"}', which is not a"
                + " count. KitchenBoard.razor renders it from KitchenAlertState.UnseenCount.");
        }

        ILocator lines = page.Locator(PendingLineSelector);
        int count = await lines.CountAsync();

        List<KitchenBoardLine> pending = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);

            // "2×" — the multiplication sign is markup, not data, so it is trimmed off rather than
            // parsed around.
            string quantityText = (await line.Locator("span.kitchen-line-quantity").First.InnerTextAsync())
                .Trim()
                .TrimEnd('×');

            string name = (await line.Locator("span.kitchen-line-name").First.InnerTextAsync()).Trim();

            ILocator note = line.Locator("p.kitchen-line-note");
            string? noteText = await note.CountAsync() > 0
                ? (await note.First.InnerTextAsync()).Trim()
                : null;

            pending.Add(new KitchenBoardLine(
                int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity)
                    ? quantity
                    : 0,
                name,
                noteText));
        }

        return new KitchenBoardSnapshot(unseenCount, pending);
    }

    /// <summary>
    /// Waits until the board satisfies <paramref name="expectation"/> and returns the snapshot that did.
    /// The predicate is the assertion; <paramref name="whatIsExpected"/> is what the failure says was
    /// wanted, beside what the board was actually showing when it gave up.
    /// </summary>
    internal static async Task<KitchenBoardSnapshot> WaitForBoardAsync(
        IPage page,
        Func<KitchenBoardSnapshot, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        KitchenBoardSnapshot observed = new(0, []);

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadBoardAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The kitchen board never showed {whatIsExpected} within {timeout.TotalSeconds:F0}s."
                + $" What it was showing: {Describe(observed)}."));
    }

    /// <summary>
    /// Taps one line on the pass — §11.2's "tap a line → one <c>fulfillment</c> event" — and returns
    /// once the board has confirmed it.
    ///
    /// <para>The line is found by reading the queue and matching the name, rather than by putting the
    /// name into a CSS selector. Menu items are free text: an apostrophe in "Chef's soup" would break a
    /// <c>:text-is('…')</c> selector, and a scenario is not the place to learn about selector escaping.</para>
    ///
    /// <para>Waiting for the confirmation is load-bearing. Every kitchen action goes through
    /// <c>IOrderWorkflow</c> and can be refused under the §6.6 lock — two cooks on one line, a guest who
    /// removed it a second earlier — and the board renders that refusal rather than throwing. A scenario
    /// that clicked and moved on would assert against a board that had refused, and the refusal is
    /// exactly the sentence it needs.</para>
    ///
    /// <para>The line leaving the pass is what is waited for, not the sentence beside it.
    /// <c>kitchen_pending_line</c> excludes a fulfilled line (§8.3), so its disappearance is the write
    /// itself rather than a report of it — and unlike <c>p.status-success</c>, which stays on screen
    /// until something clears it, it cannot be satisfied by the previous tap.</para>
    /// </summary>
    internal static async Task FulfillLineAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator lines = page.Locator(PendingLineSelector);
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.kitchen-line-name").First.InnerTextAsync()).Trim();

            if (!string.Equals(name, menuItemName, StringComparison.Ordinal))
            {
                continue;
            }

            await line.Locator("button.kitchen-line-button").First.ClickAsync();
            await WaitForLineToLeaveThePassAsync(page, menuItemName, count - 1);
            return;
        }

        KitchenBoardSnapshot board = await ReadBoardAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no line for '{menuItemName}' on the pass to fulfill."
                + $" The board is showing: {Describe(board)}."));
    }

    /// <summary>
    /// Turns a menu item off from §11.2's "86" panel — §7's "the guest sees that the salmon exists and
    /// is out" — and returns once the panel says so.
    ///
    /// <para>Completion is read from the row's own <c>is-off</c> class rather than from the flash
    /// sentence beside it. The sentence is copy and it survives the next action; the class is the state
    /// <c>MenuItemSummary.IsActive</c> rendered, which is the thing the guest's picker and the §6.5.4
    /// transaction will both read next.</para>
    ///
    /// <para>What this does <em>not</em> wait for is the guest's screen. §9's <c>MenuChanged</c> reaches
    /// every open surface, and §7 says a staged line that has gone unavailable is marked in the guest's
    /// basket — but that is the other browser's business, and a scenario that cares waits for it there
    /// (<see cref="TableOrderJourneys.BasketWarningCountAsync"/>).</para>
    /// </summary>
    internal static async Task EightySixAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!await TryPressAvailabilityToggleAsync(page, menuItemName, wantOff: true))
        {
            string menu = await DescribeMenuAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no '{menuItemName}' on the 86 panel to turn off."
                    + $" What it lists: {menu}."));
        }

        if (await WaitForAvailabilityAsync(page, menuItemName, expectedOff: true))
        {
            return;
        }

        string refusal = await DescribeRefusalAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{menuItemName}' never went off the menu within"
                + $" {AvailabilityPatience.TotalSeconds:F0}s. {refusal}"));
    }

    /// <summary>Whether the 86 panel currently shows the named item as turned off.</summary>
    internal static async Task<bool> IsEightySixedAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        bool? off = await ReadAvailabilityAsync(page, menuItemName);

        if (off is not { } isOff)
        {
            string menu = await DescribeMenuAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no '{menuItemName}' on the 86 panel. What it lists: {menu}."));
        }

        return isOff;
    }

    /// <summary>A short, quotable rendering of the board, for a failure message.</summary>
    internal static string Describe(KitchenBoardSnapshot board)
    {
        ArgumentNullException.ThrowIfNull(board);

        string lines = board.PendingLines.Count == 0
            ? "nothing outstanding"
            : string.Join(
                "; ",
                board.PendingLines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{line.Quantity} × '{line.Name}'{(line.Note is null ? string.Empty : $" note “{line.Note}”")}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{board.UnseenAlertCount} unseen alert(s), {lines}");
    }

    /// <summary>
    /// Waits for the tapped line to leave <c>kitchen_pending_line</c> (§8.3) — which is what fulfilling
    /// it does — and reports the board's own refusal if it never does.
    /// </summary>
    private static async Task WaitForLineToLeaveThePassAsync(IPage page, string menuItemName, int expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + AvailabilityPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(PendingLineSelector).CountAsync() == expected)
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        string refusal = await DescribeRefusalAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Fulfilling '{menuItemName}' did not take it off the pass. {refusal}"));
    }

    /// <summary>
    /// Presses the availability toggle on the named row, unless it is already in the wanted state.
    /// Returns false when the panel has no such row at all — which is a different failure and gets a
    /// different sentence.
    /// </summary>
    private static async Task<bool> TryPressAvailabilityToggleAsync(IPage page, string menuItemName, bool wantOff)
    {
        ILocator items = page.Locator(MenuItemSelector);
        int count = await items.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator item = items.Nth(index);
            string name = (await item.Locator("span.kitchen-menu-name").First.InnerTextAsync()).Trim();

            if (!string.Equals(name, menuItemName, StringComparison.Ordinal))
            {
                continue;
            }

            // Already there. §7's flip is idempotent at the write (SetMenuItemAvailabilityOutcome
            // .AlreadyInThatState commits nothing and announces nothing), so pressing anyway would turn
            // it back on — the opposite of what was asked for.
            if (IsOff(await item.GetAttributeAsync("class")) != wantOff)
            {
                await item.Locator("button").First.ClickAsync();
            }

            return true;
        }

        return false;
    }

    private static async Task<bool> WaitForAvailabilityAsync(IPage page, string menuItemName, bool expectedOff)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + AvailabilityPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ReadAvailabilityAsync(page, menuItemName) == expectedOff)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    /// <summary>True when off, false when on, null when the panel has no row for this item.</summary>
    private static async Task<bool?> ReadAvailabilityAsync(IPage page, string menuItemName)
    {
        ILocator items = page.Locator(MenuItemSelector);
        int count = await items.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator item = items.Nth(index);
            string name = (await item.Locator("span.kitchen-menu-name").First.InnerTextAsync()).Trim();

            if (string.Equals(name, menuItemName, StringComparison.Ordinal))
            {
                return IsOff(await item.GetAttributeAsync("class"));
            }
        }

        return null;
    }

    /// <summary>
    /// §11.2 renders an unavailable row as <c>class="kitchen-menu-item is-off"</c>. Matched as a word
    /// rather than by containment, so a future <c>is-off-peak</c> could not be mistaken for it.
    /// </summary>
    private static bool IsOff(string? classAttribute)
        => classAttribute is not null
        && classAttribute
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("is-off", StringComparer.Ordinal);

    private static async Task<string> DescribeMenuAsync(IPage page)
    {
        ILocator items = page.Locator(MenuItemSelector);
        int count = await items.CountAsync();

        if (count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"nothing at all; the browser is at '{page.Url}'");
        }

        List<string> described = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator item = items.Nth(index);
            string name = (await item.Locator("span.kitchen-menu-name").First.InnerTextAsync()).Trim();
            bool off = IsOff(await item.GetAttributeAsync("class"));

            described.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{name}' [{(off ? "86'd" : "on")}]"));
        }

        return string.Join("; ", described);
    }

    private static async Task<string> DescribeRefusalAsync(IPage page)
    {
        ILocator problems = page.Locator($"{SurfaceSelector} p.status-error");

        if (await problems.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The board reports no refusal; the browser is at '{page.Url}'.");
        }

        string message = (await problems.First.InnerTextAsync()).Trim();

        return string.Create(CultureInfo.InvariantCulture, $"The board says: {message}");
    }

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(SurfaceSelector);

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no kitchen board on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.First.GetAttributeAsync("data-live");
        string? unseen = await surface.First.GetAttributeAsync("data-unseen-alerts");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"data-live='{live ?? "absent"}', data-unseen-alerts='{unseen ?? "absent"}'");
    }
}
