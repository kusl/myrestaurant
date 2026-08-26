using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Orders;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record KitchenBoardLine(int Quantity, string Name, string? Note);

internal sealed record KitchenBoardSnapshot(
    int UnseenAlertCount,
    int UnseenReminderCount,
    IReadOnlyList<KitchenBoardLine> PendingLines);

internal static class KitchenJourneys
{
    internal const string BoardPath = "/kitchen";

    private const string SurfaceSelector = "#kitchen-board-surface";

    private const string LiveSurfaceSelector =
        "#kitchen-board-surface[data-live='true'][data-loaded='true']";

    private const string PendingLineSelector = "#kitchen-board-surface li.kitchen-line";

    private const string MenuItemSelector = "#kitchen-board-surface ul.kitchen-menu > li.kitchen-menu-item";

    private const string AlertBadgeSelector = "#kitchen-board-surface button.kitchen-alert-badge";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan AvailabilityPatience = TimeSpan.FromSeconds(30);

    internal static async Task OpenAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(BoardPath);
        await WaitForLiveBoardAsync(page, timeout);
    }

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
                    $"The kitchen board was not live and loaded within {timeout.TotalSeconds:F0}s"
                    + $" ({surface}). A surface present with data-live='false' is still the prerendered"
                    + $" markup: it will list whatever was outstanding when the page was requested and"
                    + $" then never change and never alert, because §9's broadcasts go to subscribers and"
                    + $" the subscription is made on the circuit — check that /_framework/blazor.web.js"
                    + $" is served and that the browser reached /_blazor. One stuck at"
                    + $" data-loaded='false' has a circuit and is waiting on §11.2's three queries."),
                exception);
        }
    }

    internal static async Task<KitchenBoardSnapshot> ReadBoardAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator surface = page.Locator(SurfaceSelector).First;

        int unseenCount = await ReadCountAttributeAsync(
            surface, "data-unseen-alerts", nameof(KitchenAlertState.UnseenCount));

        int unseenReminderCount = await ReadCountAttributeAsync(
            surface,
            "data-unseen-reminders",
            nameof(KitchenAlertState.UnseenReminderCount));

        ILocator lines = page.Locator(PendingLineSelector);
        int count = await lines.CountAsync();

        List<KitchenBoardLine> pending = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);

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

        return new KitchenBoardSnapshot(unseenCount, unseenReminderCount, pending);
    }

    private static async Task<int> ReadCountAttributeAsync(
        ILocator surface,
        string attributeName,
        string sourcePropertyName)
    {
        string? raw = await surface.GetAttributeAsync(attributeName);

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The kitchen board published {attributeName}='{raw ?? "absent"}', which is not a count."
                + $" KitchenBoard.razor renders it from KitchenAlertState.{sourcePropertyName}."));
    }

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
        KitchenBoardSnapshot observed = new(0, 0, []);

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

    internal static async Task AcknowledgeAlertsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator badge = page.Locator(AlertBadgeSelector);

        if (await badge.CountAsync() == 0)
        {
            KitchenBoardSnapshot board = await ReadBoardAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no alert badge to clear: §10.3 renders it only while something is"
                    + $" unseen, so nothing has arrived at this board that anybody could acknowledge."
                    + $" It is showing: {Describe(board)}."));
        }

        await badge.First.ClickAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + AvailabilityPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ReadBoardAsync(page)).UnseenAlertCount == 0)
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        KitchenBoardSnapshot stubborn = await ReadBoardAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Tapping the alert badge did not clear it within"
                + $" {AvailabilityPatience.TotalSeconds:F0}s. The board is showing: {Describe(stubborn)}."));
    }

    internal static async Task<KitchenBoardSnapshot> WatchBoardAsync(
        IPage page,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + duration;

        int highestAlerts = 0;
        int highestReminders = 0;
        KitchenBoardSnapshot observed = await ReadBoardAsync(page);

        while (true)
        {
            highestAlerts = Math.Max(highestAlerts, observed.UnseenAlertCount);
            highestReminders = Math.Max(highestReminders, observed.UnseenReminderCount);

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new KitchenBoardSnapshot(highestAlerts, highestReminders, observed.PendingLines);
            }

            await Task.Delay(PollInterval, cancellationToken);
            observed = await ReadBoardAsync(page);
        }
    }

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

        string overdue = board.UnseenReminderCount == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $" ({board.UnseenReminderCount} of them overdue reminders)");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{board.UnseenAlertCount} unseen alert(s){overdue}, {lines}");
    }

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
        string? loaded = await surface.First.GetAttributeAsync("data-loaded");
        string? unseen = await surface.First.GetAttributeAsync("data-unseen-alerts");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"data-live='{live ?? "absent"}', data-loaded='{loaded ?? "absent"}',"
            + $" data-unseen-alerts='{unseen ?? "absent"}'");
    }
}
