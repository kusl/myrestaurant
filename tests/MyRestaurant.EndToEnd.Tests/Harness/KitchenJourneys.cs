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
/// The kitchen journeys the §16.3 scenarios walk: opening the board, watching what arrives on it, and
/// marking a line away (TECHNICAL_SPECIFICATION §10, §11.2).
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

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

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
            await WaitForActionConfirmationAsync(page, menuItemName);
            return;
        }

        KitchenBoardSnapshot board = await ReadBoardAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no line for '{menuItemName}' on the pass to fulfill."
                + $" The board is showing: {Describe(board)}."));
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

    private static async Task WaitForActionConfirmationAsync(IPage page, string menuItemName)
    {
        ILocator confirmation = page.Locator($"{SurfaceSelector} p.status-success").First;

        try
        {
            await confirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeRefusalAsync(page);

            throw new InvalidOperationException(
                $"Fulfilling '{menuItemName}' was not confirmed. {refusal}",
                exception);
        }
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
