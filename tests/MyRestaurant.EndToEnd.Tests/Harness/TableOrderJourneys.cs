using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record GuestOrderLine(string Name, GuestLineBadge Badge);

internal enum GuestLineBadge
{
    WithTheKitchen,
    AtYourTable,
    Removed,
}

internal sealed record GuestPriceAdjustment(
    string PreviousPriceText,
    string NewPriceText,
    string Sentence);

internal sealed record GuestOrderLineDetail(
    string Name,
    GuestLineBadge Badge,
    string PriceText,
    IReadOnlyList<GuestPriceAdjustment> PriceAdjustments);

internal sealed record TableRosterMember(string Name, bool IsYou);

internal sealed record PartyOrder(string BillName, string TotalText, IReadOnlyList<GuestOrderLine> Lines);

internal sealed record MenuCard(
    string SectionName,
    string Name,
    string PriceText,
    string? Description,
    bool IsAvailable,
    bool IsChosen);

internal sealed record ChosenItemDetail(
    string Name,
    string? Description,
    IReadOnlyDictionary<string, string> Facts);

internal sealed record BasketContents(int StagedAdds, int TickedRemovals, int UnavailableMarks);

internal sealed record GuestTotals(string YourTotalText, string TableTotalText);

internal sealed record GuestSettledView(
    bool SaysSettled,
    bool OffersPicker,
    bool OffersSend,
    int RemovalCheckboxes,
    GuestTotals Totals,
    IReadOnlyList<GuestOrderLineDetail> Lines);

internal sealed record SendOutcome(
    bool BasketIsEmpty,
    string? Confirmation,
    IReadOnlyList<string> RefusalReasons);

internal static class TableOrderJourneys
{
    private const string SurfaceSelector = "#table-order-surface";

    private const string LiveSurfaceSelector =
        "#table-order-surface[data-live='true'][data-loaded='true']";

    private const string BasketLineSelector =
        "#table-order-surface ul.order-basket li.order-basket-line:not(.is-removal)";

    private const string BasketRemovalSelector =
        "#table-order-surface ul.order-basket li.order-basket-line.is-removal";

    private const string CommittedLineSelector = "#table-order-surface ul.order-lines li.order-line";

    private const string PriceAdjustmentSelector = "p.order-line-adjustment";

    private const string RosterMemberSelector = "#table-order-surface ul.table-roster > li";

    private const string PartyOrderSelector = "#table-order-surface ul.order-party > li.order-party-order";

    private const string RefusalReasonSelector = "#table-order-surface ul.order-reject-list li";

    private const string PruneNoticeSelector = "#table-order-surface p.order-prune-notice";

    private const string ConfirmationSelector = "#table-order-surface p.status-success";

    private const string SettledHeadingSelector = "#table-order-surface h2.order-settled-heading";

    private const string PickerSelector = "#table-order-surface div.order-picker";
    private const string SendRowSelector = "#table-order-surface .order-send";

    private const string MenuCardSelector =
        "#table-order-surface div.order-menu-section ul.order-menu button.order-menu-choice";

    private const string MenuSectionSelector = "#table-order-surface div.order-menu-section";

    private const string MenuInspectSelector =
        "#table-order-surface li.order-menu-item > button.order-menu-inspect";

    private const string MenuDetailSelector = "#table-order-surface div.order-menu-detail";

    private const string LikeControlSelector =
        "#table-order-surface div.order-menu-detail button.order-menu-like";

    private const string CommentBoxSelector =
        "#table-order-surface div.order-menu-detail textarea.order-menu-comment-body";

    private const string CommentSaveSelector =
        "#table-order-surface div.order-menu-detail button.order-menu-comment-save";

    private const string CommentWithdrawSelector =
        "#table-order-surface div.order-menu-detail button.order-menu-comment-withdraw";

    private const string CommentNoticeSelector =
        "#table-order-surface div.order-menu-detail p.order-menu-comment-notice";

    private const string CommentOutcomeAttribute = "data-comment-outcome";

    private const string RemovalCheckboxSelector = "#table-order-surface label.order-line-remove";

    private const string UnavailableMarkSelector = "#table-order-surface p.order-line-warning";

    private const string TotalsGroupSelector = "#table-order-surface dl.order-totals > div";

    private const string YourTotalTerm = "Your total";
    private const string TableTotalTerm = "Table total";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SendPatience = TimeSpan.FromSeconds(30);

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
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The ordering surface was not live and loaded within"
                    + $" {timeout.TotalSeconds:F0}s ({surface}). A surface present with"
                    + $" data-live='false' is still the prerendered markup: nothing on the page will"
                    + $" respond — Add to basket, Send and every quantity box are @onclick handlers with"
                    + $" no circuit behind them, and the kitchen will never hear anything. Check that"
                    + $" /_framework/blazor.web.js is served (RestaurantInstance probes it at startup)"
                    + $" and that the browser reached /_blazor. One stuck at data-loaded='false' has a"
                    + $" circuit and is still on §11.1's reads."),
                exception);
        }
    }

    internal static async Task StageAsync(
        IPage page,
        MenuItemOnTheMenu item,
        int quantity,
        string? customizationNote = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(item);

        int before = await page.Locator(BasketLineSelector).CountAsync();

        await ChooseAsync(page, item);

        await page.FillAsync(
            "#order-picker-quantity",
            quantity.ToString(CultureInfo.InvariantCulture));

        await page.FillAsync("#order-picker-note", customizationNote ?? string.Empty);

        string addToBasket = $"{SurfaceSelector} .order-picker button:has-text('Add to basket')";

        await page.FocusAsync(addToBasket);
        await page.ClickAsync(addToBasket);

        if (await WaitForCountAsync(page, BasketLineSelector, before + 1, TimeSpan.FromSeconds(15)))
        {
            return;
        }

        int after = await page.Locator(BasketLineSelector).CountAsync();
        string refusal = await DescribeStagingRefusalAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Staging {quantity} × '{item.Name}' did not put a line in the basket:"
                + $" it holds {after} line(s) rather than {before + 1}. {refusal}"));
    }

    internal static async Task ChooseAsync(IPage page, MenuItemOnTheMenu item)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(item);

        string identifier = item.Identifier.ToString("D", CultureInfo.InvariantCulture);
        string card = string.Create(
            CultureInfo.InvariantCulture,
            $"{SurfaceSelector} button.order-menu-choice[data-menu-item='{identifier}']");

        ILocator choice = page.Locator(card).First;

        if (await choice.CountAsync() == 0)
        {
            string menu = Describe(await ReadMenuAsync(page));

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{item.Name}' is not on the menu this surface is showing. What is: {menu}."
                    + $" §7 keeps a deactivated item ON the menu, so an absent card means the item was"
                    + $" never created, or that this circuit has not re-read since it was (§9's"
                    + $" MenuChanged)."));
        }

        if (await choice.IsDisabledAsync())
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{item.Name}' is on the menu and marked unavailable, so its card is disabled and"
                    + $" cannot be chosen (§7). Something deactivated it — the kitchen's 86 panel"
                    + $" (§11.2) is the surface that does — and a scenario that wants this item orderable"
                    + $" has to put it back first."));
        }

        await choice.ClickAsync();

        if (await WaitForAttributeAsync(page, card, "aria-pressed", "true", TimeSpan.FromSeconds(15)))
        {
            return;
        }

        string showing = Describe(await ReadMenuAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Tapping '{item.Name}' did not choose it: the card never reported"
                + $" aria-pressed='true'. The menu shows: {showing}. A card that does not answer a tap is"
                + $" the @onclick landing on no circuit, which WaitForLiveSurfaceAsync is supposed to have"
                + $" ruled out before this."));
    }

    internal static async Task InspectAsync(IPage page, MenuItemOnTheMenu item)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(item);

        string identifier = item.Identifier.ToString("D", CultureInfo.InvariantCulture);
        string control = string.Create(
            CultureInfo.InvariantCulture,
            $"{MenuInspectSelector}[data-menu-item-inspect='{identifier}']");

        ILocator inspect = page.Locator(control).First;

        if (await inspect.CountAsync() == 0)
        {
            string menu = Describe(await ReadMenuAsync(page));

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no way-in control for '{item.Name}' beside its card. §11.1 renders one"
                    + $" only where §7 has refused the card — a dish that is ON the menu is opened by"
                    + $" tapping the card itself, which is ChooseAsync. What the menu shows: {menu}."));
        }

        await inspect.ClickAsync();

        if (await WaitForAttributeAsync(page, control, "aria-pressed", "true", TimeSpan.FromSeconds(15)))
        {
            return;
        }

        string showing = Describe(await ReadMenuAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Pressing the way-in control for '{item.Name}' did not open its panel: the control"
                + $" never reported aria-pressed='true'. The menu shows: {showing}. A control that does"
                + $" not answer a tap is the @onclick landing on no circuit, which"
                + $" WaitForLiveSurfaceAsync is supposed to have ruled out before this."));
    }

    internal static async Task<IReadOnlyList<MenuCard>> ReadMenuAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator sections = page.Locator(MenuSectionSelector);
        int sectionCount = await sections.CountAsync();

        List<MenuCard> menu = [];

        for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            ILocator section = sections.Nth(sectionIndex);

            string sectionName = (await section
                .Locator("h4.order-menu-section-name").First.TextContentAsync() ?? string.Empty).Trim();

            ILocator cards = section.Locator("button.order-menu-choice");
            int count = await cards.CountAsync();

            for (int index = 0; index < count; index++)
            {
                ILocator card = cards.Nth(index);

                string name = (await card.Locator("span.order-menu-name").First.InnerTextAsync()).Trim();
                string price = (await card.Locator("span.order-menu-price").First.InnerTextAsync()).Trim();

                ILocator description = card.Locator("span.order-menu-description");
                string? descriptionText = await description.CountAsync() == 0
                    ? null
                    : (await description.First.InnerTextAsync()).Trim();

                bool isAvailable = !await card.IsDisabledAsync();

                bool isChosen = string.Equals(
                    await card.GetAttributeAsync("aria-pressed"),
                    "true",
                    StringComparison.Ordinal);

                menu.Add(new MenuCard(sectionName, name, price, descriptionText, isAvailable, isChosen));
            }
        }

        return menu;
    }

    internal static async Task<IReadOnlyList<string>> ReadMenuSectionNamesAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return (await ReadMenuAsync(page))
            .Select(card => card.SectionName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static async Task<IReadOnlyList<(string SectionName, string? Description)>>
        ReadMenuSectionDescriptionsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator sections = page.Locator(MenuSectionSelector);
        int sectionCount = await sections.CountAsync();

        List<(string SectionName, string? Description)> headings = [];

        for (int index = 0; index < sectionCount; index++)
        {
            ILocator section = sections.Nth(index);

            string name = (await section
                .Locator("h4.order-menu-section-name").First.TextContentAsync() ?? string.Empty).Trim();

            ILocator description = section.Locator("p.order-menu-section-description");

            string? text = await description.CountAsync() == 0
                ? null
                : (await description.First.TextContentAsync() ?? string.Empty).Trim();

            headings.Add((name, text));
        }

        return headings;
    }

    internal static async Task<ChosenItemDetail?> ReadChosenItemDetailAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator panel = page.Locator(MenuDetailSelector);

        if (await panel.CountAsync() == 0)
        {
            return null;
        }

        ILocator first = panel.First;

        string name = (await first.Locator("h4.order-menu-detail-name").First.InnerTextAsync()).Trim();

        ILocator description = first.Locator("p.order-menu-detail-description");
        string? descriptionText = await description.CountAsync() == 0
            ? null
            : (await description.First.InnerTextAsync()).Trim();

        Dictionary<string, string> facts = new(StringComparer.Ordinal);
        ILocator groups = first.Locator("dl.order-menu-facts > div");
        int count = await groups.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator group = groups.Nth(index);

            string term = await ScreenText.DeclaredAsync(group.Locator("dt").First);
            string value = (await group.Locator("dd").First.InnerTextAsync()).Trim();

            facts[term] = value;
        }

        return new ChosenItemDetail(name, descriptionText, facts);
    }

    internal static async Task<bool?> ReadChosenItemLikedAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator control = page.Locator(LikeControlSelector);

        if (await control.CountAsync() == 0)
        {
            return null;
        }

        string? pressed = await control.First.GetAttributeAsync("aria-pressed");

        return pressed switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§11.1's like control carries aria-pressed=\"{pressed}\", which is neither"
                    + $" \"true\" nor \"false\". A toggle button states its state there or states it"
                    + $" nowhere a user agent can read.")),
        };
    }

    internal static async Task<bool> PressLikeAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        bool? before = await ReadChosenItemLikedAsync(page);

        if (before is not { } was)
        {
            throw new InvalidOperationException(
                "There is no like control to press: §11.1 renders it inside the detail panel, so an item"
                + " has to be chosen first.");
        }

        await page.Locator(LikeControlSelector).First.ClickAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ReadChosenItemLikedAsync(page) is { } now && now != was)
            {
                return now;
            }

            await Task.Delay(PollInterval);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The like control still reports {was} {timeout.TotalSeconds:F0}s after it was pressed."
                + $" A press writes one row and re-renders this island; nothing about it waits on a"
                + $" broadcast, because a reaction deliberately publishes none (§9)."));
    }

    internal static async Task<string?> ReadChosenItemCommentAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator box = page.Locator(CommentBoxSelector);

        if (await box.CountAsync() == 0)
        {
            return null;
        }

        return await box.First.InputValueAsync();
    }

    internal static async Task<string> SaveCommentAsync(IPage page, string body, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator box = page.Locator(CommentBoxSelector);

        if (await box.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                "There is no comment box to write in: §11.1 renders it inside the detail panel beside"
                + " the like, so a dish has to be chosen first.");
        }

        string? before = await ReadCommentOutcomeAsync(page);

        await box.First.FillAsync(body);
        await page.Locator(CommentSaveSelector).First.ClickAsync();

        return await WaitForCommentOutcomeAsync(page, before, timeout, "saving a comment");
    }

    internal static async Task<string> WithdrawCommentAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator control = page.Locator(CommentWithdrawSelector);

        if (await control.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                "There is no withdraw control in this panel. §11.1 renders it only where a standing"
                + " comment exists, because §7's withdrawal verb refuses when there is nothing to"
                + " withdraw — so a scenario that wants to press it has to have saved something"
                + " first.");
        }

        string? before = await ReadCommentOutcomeAsync(page);

        await control.First.ClickAsync();

        return await WaitForCommentOutcomeAsync(page, before, timeout, "withdrawing a comment");
    }

    private static async Task<string?> ReadCommentOutcomeAsync(IPage page)
    {
        ILocator notice = page.Locator(CommentNoticeSelector);

        if (await notice.CountAsync() == 0)
        {
            return null;
        }

        return await notice.First.GetAttributeAsync(CommentOutcomeAttribute);
    }

    private static async Task<string> WaitForCommentOutcomeAsync(
        IPage page,
        string? before,
        TimeSpan timeout,
        string whatWasPressed)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ReadCommentOutcomeAsync(page) is { } outcome
                && !string.Equals(outcome, before, StringComparison.Ordinal))
            {
                return outcome;
            }

            await Task.Delay(PollInterval);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"§11.1 reported no new outcome {timeout.TotalSeconds:F0}s after {whatWasPressed}"
                + $" (it still reads '{before ?? "nothing"}'). The surface declares the verdict beside"
                + $" the sentence so a scenario reads the outcome rather than the copywriting; a press"
                + $" that produces neither is the @onclick landing on no circuit, which"
                + $" WaitForLiveSurfaceAsync is supposed to have ruled out before this."));
    }

    internal static async Task<IReadOnlyList<MenuCard>> WaitForMenuAsync(
        IPage page,
        Func<IReadOnlyList<MenuCard>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<MenuCard> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadMenuAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The menu never showed {whatIsExpected} within {timeout.TotalSeconds:F0}s."
                + $" What it shows: {Describe(observed)}."));
    }

    internal static async Task UnstageAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator staged = page.Locator(BasketLineSelector);
        int before = await staged.CountAsync();

        for (int index = 0; index < before; index++)
        {
            ILocator line = staged.Nth(index);
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            if (!name.Contains(menuItemName, StringComparison.Ordinal))
            {
                continue;
            }

            await line.Locator("button:has-text('Take out')").First.ClickAsync();

            if (await WaitForCountAsync(page, BasketLineSelector, before - 1, TimeSpan.FromSeconds(15)))
            {
                return;
            }

            int after = await page.Locator(BasketLineSelector).CountAsync();

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Taking '{menuItemName}' out of the basket left it holding {after} line(s)"
                    + $" rather than {before - 1}."));
        }

        string basket = await DescribeBasketAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no staged '{menuItemName}' in the basket to take out."
                + $" What is staged: {basket}."));
    }

    internal static async Task MarkForRemovalAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        int before = await page.Locator(BasketRemovalSelector).CountAsync();
        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            if (!name.Contains(menuItemName, StringComparison.Ordinal))
            {
                continue;
            }

            ILocator tick = line.Locator("label.order-line-remove input[type='checkbox']");

            if (await tick.CountAsync() == 0)
            {
                GuestLineBadge badge = await ReadBadgeAsync(line);

                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The line '{name}' offers no way to take it off: §11.1 renders the tick box"
                        + $" only while GuestMayRemove holds (pending, added by this guest's own"
                        + $" submission — §6.5.3), so the surface has already decided this one is not"
                        + $" the guest's to remove. The line is badged {badge}."));
            }

            await tick.First.CheckAsync();

            if (await WaitForCountAsync(page, BasketRemovalSelector, before + 1, TimeSpan.FromSeconds(15)))
            {
                return;
            }

            int after = await page.Locator(BasketRemovalSelector).CountAsync();

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Ticking '{name}' for removal did not reach the basket: it holds {after}"
                    + $" removal(s) rather than {before + 1}."));
        }

        string committed = Describe(await ReadCommittedLinesAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no committed line for '{menuItemName}' to take off."
                + $" The order holds: {committed}."));
    }

    internal static async Task<bool> LineOffersRemovalAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            if (name.Contains(menuItemName, StringComparison.Ordinal))
            {
                return await line.Locator("label.order-line-remove").CountAsync() > 0;
            }
        }

        string committed = Describe(await ReadCommittedLinesAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no committed line for '{menuItemName}' on this order."
                + $" It holds: {committed}."));
    }

    internal static async Task<string> SendAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        SendOutcome outcome = await PressSendAsync(page);

        if (outcome.RefusalReasons.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The send was refused, so nothing was written and the basket is untouched (§6.5.9)."
                    + $" The surface says: {string.Join(" | ", outcome.RefusalReasons)}"));
        }

        return outcome.Confirmation ?? string.Empty;
    }

    internal static async Task<IReadOnlyList<string>> SendExpectingRefusalAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        SendOutcome outcome = await PressSendAsync(page);

        if (outcome.RefusalReasons.Count > 0)
        {
            return outcome.RefusalReasons;
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The send was accepted when §6.5.9 should have refused the whole batch. The surface"
                + $" says: '{outcome.Confirmation ?? "(nothing)"}', and the staging area"
                + $" {(outcome.BasketIsEmpty ? "has been cleared, so the event was written" : "is still full, so this is neither outcome")}."));
    }

    internal static Task<int> BasketLineCountAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Locator(BasketLineSelector).CountAsync();
    }

    internal static Task<int> BasketRemovalCountAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Locator(BasketRemovalSelector).CountAsync();
    }

    internal static async Task<BasketContents> ReadBasketAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        string[] selectors =
        [
            BasketLineSelector,
            BasketRemovalSelector,
            UnavailableMarkSelector,
        ];

        JsonElement? evaluated = await page.EvaluateAsync(CountingScript, selectors);

        if (evaluated is not { ValueKind: JsonValueKind.Array } counted
            || counted.GetArrayLength() != selectors.Length)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Counting the basket returned no reading for {selectors.Length} selector(s), so"
                    + $" this reading is of no instant at all; the browser is at '{page.Url}'."));
        }

        return new BasketContents(
            counted[0].GetInt32(),
            counted[1].GetInt32(),
            counted[2].GetInt32());
    }

    internal static async Task<string?> ReadPruneNoticeAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator notice = page.Locator(PruneNoticeSelector);

        return await notice.CountAsync() == 0
            ? null
            : (await notice.First.InnerTextAsync()).Trim();
    }

    internal static async Task<IReadOnlyList<GuestOrderLine>> ReadCommittedLinesAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        IReadOnlyList<GuestOrderLineDetail> detailed = await ReadOwnLinesAsync(page);

        return detailed
            .Select(line => new GuestOrderLine(line.Name, line.Badge))
            .ToArray();
    }

    internal static async Task<IReadOnlyList<GuestOrderLineDetail>> ReadOwnLinesAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        List<GuestOrderLineDetail> committed = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);

            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();
            string price = (await line.Locator("span.order-line-price").First.InnerTextAsync()).Trim();

            committed.Add(new GuestOrderLineDetail(
                name,
                await ReadBadgeAsync(line),
                price,
                await ReadPriceAdjustmentsAsync(line)));
        }

        return committed;
    }

    internal static async Task<GuestOrderLineDetail> WaitForOwnLineAsync(
        IPage page,
        string menuItemName,
        Func<GuestOrderLineDetail, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<GuestOrderLineDetail> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadOwnLinesAsync(page);

            GuestOrderLineDetail? named = observed.FirstOrDefault(
                line => line.Name.Contains(menuItemName, StringComparison.Ordinal));

            if (named is not null && expectation(named))
            {
                return named;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The guest's line for '{menuItemName}' never showed {whatIsExpected} within"
                + $" {timeout.TotalSeconds:F0}s. What the order shows: {DescribeOwn(observed)}."));
    }

    internal static async Task<GuestTotals> ReadTotalsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator groups = page.Locator(TotalsGroupSelector);
        int count = await groups.CountAsync();

        Dictionary<string, string> byTerm = new(StringComparer.Ordinal);

        for (int index = 0; index < count; index++)
        {
            ILocator group = groups.Nth(index);

            string term = await ScreenText.DeclaredAsync(group.Locator("dt").First);
            string amount = (await group.Locator("dd").First.InnerTextAsync()).Trim();

            byTerm[term] = amount;
        }

        if (!byTerm.TryGetValue(YourTotalTerm, out string? yourTotal)
            || !byTerm.TryGetValue(TableTotalTerm, out string? tableTotal))
        {
            string terms = byTerm.Count == 0
                ? "nothing at all"
                : string.Join(", ", byTerm.Keys.Select(term => $"'{term}'"));

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§11.1's totals list does not carry both '{YourTotalTerm}' and '{TableTotalTerm}'."
                    + $" What it names: {terms}. The browser is at '{page.Url}'."));
        }

        return new GuestTotals(yourTotal, tableTotal);
    }

    internal static async Task<GuestSettledView> ReadSettledViewAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new GuestSettledView(
            await page.Locator(SettledHeadingSelector).CountAsync() > 0,
            await page.Locator(PickerSelector).CountAsync() > 0,
            await page.Locator(SendRowSelector).CountAsync() > 0,
            await page.Locator(RemovalCheckboxSelector).CountAsync(),
            await ReadTotalsAsync(page),
            await ReadOwnLinesAsync(page));
    }

    internal static async Task<GuestSettledView> WaitForSettledViewAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(SettledHeadingSelector).CountAsync() > 0)
            {
                return await ReadSettledViewAsync(page);
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        string surface = await DescribeSurfaceAsync(page);
        IReadOnlyList<GuestOrderLineDetail> lines = await ReadOwnLinesAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The guest's surface never flipped to §11.1's settled view within"
                + $" {timeout.TotalSeconds:F0}s. §9 publishes SittingClosed after the close commits and"
                + $" this surface subscribes to it, so either the broadcast never left the till's circuit"
                + $" or this one is not listening ({surface}). The order still shows:"
                + $" {DescribeOwn(lines)}."));
    }

    internal static string DescribeSettledView(GuestSettledView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        string offered = view.OffersPicker || view.OffersSend || view.RemovalCheckboxes > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(view.OffersPicker ? "the picker" : "no picker")},"
                + $" {(view.OffersSend ? "a Send row" : "no Send row")},"
                + $" {view.RemovalCheckboxes} removal tick(s)")
            : "no picker, no Send row and no removal ticks";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"settled heading {(view.SaysSettled ? "present" : "absent")}; {offered};"
            + $" yours {view.Totals.YourTotalText}, table {view.Totals.TableTotalText};"
            + $" lines: {DescribeOwn(view.Lines)}");
    }

    internal static string DescribeOwn(IReadOnlyList<GuestOrderLineDetail> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing at all"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{line.Name}' {line.PriceText} [{line.Badge}]{DescribeAdjustments(line)}")));
    }

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

    internal static async Task<BasketContents> WaitForBasketAsync(
        IPage page,
        Func<BasketContents, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        BasketContents observed = new(0, 0, 0);

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadBasketAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The basket never showed {whatIsExpected} within {timeout.TotalSeconds:F0}s."
                + $" It holds {observed.StagedAdds} staged add(s), {observed.TickedRemovals} ticked"
                + $" removal(s) and {observed.UnavailableMarks} unavailable mark(s)."));
    }

    internal static async Task<IReadOnlyList<TableRosterMember>> ReadRosterAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator members = page.Locator(RosterMemberSelector);
        int count = await members.CountAsync();

        List<TableRosterMember> roster = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator member = members.Nth(index);

            string name = (await member.Locator("span.table-roster-name").First.InnerTextAsync()).Trim();

            bool isYou = await member.Locator("span.chip").CountAsync() > 0;

            roster.Add(new TableRosterMember(name, isYou));
        }

        return roster;
    }

    internal static async Task<IReadOnlyList<TableRosterMember>> WaitForRosterAsync(
        IPage page,
        Func<IReadOnlyList<TableRosterMember>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<TableRosterMember> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadRosterAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The table roster never showed {whatIsExpected} within {timeout.TotalSeconds:F0}s."
                + $" Who it says is here: {DescribeRoster(observed)}."));
    }

    internal static async Task<IReadOnlyList<PartyOrder>> ReadPartyAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator orders = page.Locator(PartyOrderSelector);
        int count = await orders.CountAsync();

        List<PartyOrder> party = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator order = orders.Nth(index);

            ILocator header = order.Locator("div.order-line-main").First;

            string billName = (await header.Locator("span.order-line-name").First.InnerTextAsync()).Trim();
            string total = (await header.Locator("span.order-line-price").First.InnerTextAsync()).Trim();

            ILocator lines = order.Locator("ul.order-party-lines > li");
            int lineCount = await lines.CountAsync();

            List<GuestOrderLine> theirLines = new(lineCount);

            for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                ILocator line = lines.Nth(lineIndex);

                string name = (await line.Locator("span.order-party-line-name").First.InnerTextAsync()).Trim();

                theirLines.Add(new GuestOrderLine(name, await ReadBadgeAsync(line)));
            }

            party.Add(new PartyOrder(billName, total, theirLines));
        }

        return party;
    }

    internal static async Task<IReadOnlyList<PartyOrder>> WaitForPartyAsync(
        IPage page,
        Func<IReadOnlyList<PartyOrder>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<PartyOrder> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadPartyAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The rest of the table never showed {whatIsExpected} within"
                + $" {timeout.TotalSeconds:F0}s. What it shows instead: {DescribeParty(observed)}."));
    }

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

    internal static string Describe(IReadOnlyList<MenuCard> menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        return menu.Count == 0
            ? "nothing at all"
            : string.Join(
                "; ",
                menu.Select(card => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{card.Name}' under '{card.SectionName}' at {card.PriceText}"
                    + $"{(card.IsAvailable ? string.Empty : " (unavailable)")}"
                    + $"{(card.IsChosen ? " (chosen)" : string.Empty)}"
                    + $"{(card.Description is null ? " with no description" : " described")}")));
    }

    internal static string DescribeRoster(IReadOnlyList<TableRosterMember> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        return roster.Count == 0
            ? "nobody at all"
            : string.Join(
                "; ",
                roster.Select(member => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{member.Name}'{(member.IsYou ? " (you)" : string.Empty)}")));
    }

    internal static string DescribeParty(IReadOnlyList<PartyOrder> party)
    {
        ArgumentNullException.ThrowIfNull(party);

        return party.Count == 0
            ? "nobody else has ordered"
            : string.Join(
                " | ",
                party.Select(entry => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{entry.BillName}' {entry.TotalText}: {Describe(entry.Lines)}")));
    }

    private static async Task<SendOutcome> PressSendAsync(IPage page)
    {
        int stagedBefore = await BasketLineCountAsync(page);
        int removalsBefore = await BasketRemovalCountAsync(page);

        if (stagedBefore + removalsBefore == 0)
        {
            throw new InvalidOperationException(
                "Send was pressed on an empty basket. §11.1 disables the button while the staging area"
                + " is empty, so this would have hung on an element that is never enabled — stage"
                + " something, or tick a line for removal, first.");
        }

        IReadOnlyList<string> stale = await ReadRefusalReasonsAsync(page);

        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§6.5.9's refusal panel from an earlier send is still on screen at the moment Send"
                    + $" was pressed, so this send's outcome cannot be told apart from the last one's."
                    + $" It says: {string.Join(" | ", stale)}"));
        }

        await page.ClickAsync($"{SurfaceSelector} .order-send button");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + SendPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<string> refusals = await ReadRefusalReasonsAsync(page);

            if (refusals.Count > 0)
            {
                return new SendOutcome(false, await ReadConfirmationAsync(page), refusals);
            }

            if (await BasketLineCountAsync(page) == 0 && await BasketRemovalCountAsync(page) == 0)
            {
                return new SendOutcome(true, await ReadConfirmationAsync(page), []);
            }

            await Task.Delay(PollInterval);
        }

        string surface = await DescribeSurfaceAsync(page);
        string basket = await DescribeBasketAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Send neither committed nor refused within {SendPatience.TotalSeconds:F0}s: the basket"
                + $" still holds {basket} and there is no rejection panel, so the click may never have"
                + $" been dispatched at all ({surface}); the browser is at '{page.Url}'."));
    }

    private static async Task<IReadOnlyList<string>> ReadRefusalReasonsAsync(IPage page)
    {
        ILocator reasons = page.Locator(RefusalReasonSelector);

        if (await reasons.CountAsync() == 0)
        {
            return [];
        }

        IReadOnlyList<string> all = await reasons.AllInnerTextsAsync();

        return all.Select(text => text.Trim()).ToArray();
    }

    private static async Task<string?> ReadConfirmationAsync(IPage page)
    {
        ILocator confirmation = page.Locator(ConfirmationSelector);

        return await confirmation.CountAsync() == 0
            ? null
            : (await confirmation.First.InnerTextAsync()).Trim();
    }

    private static async Task<IReadOnlyList<GuestPriceAdjustment>> ReadPriceAdjustmentsAsync(ILocator line)
    {
        ILocator paragraphs = line.Locator(PriceAdjustmentSelector);
        int count = await paragraphs.CountAsync();

        if (count == 0)
        {
            return [];
        }

        List<GuestPriceAdjustment> adjustments = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator paragraph = paragraphs.Nth(index);

            string sentence = (await paragraph.InnerTextAsync()).Trim();

            ILocator previous = paragraph.Locator("s");
            ILocator current = paragraph.Locator("strong");

            int previousCount = await previous.CountAsync();
            int currentCount = await current.CountAsync();

            if (previousCount == 0 || currentCount == 0)
            {
                string missing = previousCount == 0
                    ? "the struck-through old price"
                    : "the new price";

                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"§11.1 requires a price adjustment to be shown old → new, and this one is"
                        + $" missing {missing}. The sentence on screen reads: '{sentence}'."));
            }

            adjustments.Add(new GuestPriceAdjustment(
                (await previous.First.InnerTextAsync()).Trim(),
                (await current.First.InnerTextAsync()).Trim(),
                sentence));
        }

        return adjustments;
    }

    private static string DescribeAdjustments(GuestOrderLineDetail line)
    {
        if (line.PriceAdjustments.Count == 0)
        {
            return string.Empty;
        }

        string adjustments = string.Join(
            ", ",
            line.PriceAdjustments.Select(adjustment => string.Create(
                CultureInfo.InvariantCulture,
                $"{adjustment.PreviousPriceText} → {adjustment.NewPriceText}")));

        return string.Create(CultureInfo.InvariantCulture, $" adjusted {adjustments}");
    }

    private static async Task<GuestLineBadge> ReadBadgeAsync(ILocator line)
    {
        if (await line.Locator("span.chip-warn").CountAsync() > 0)
        {
            return GuestLineBadge.Removed;
        }

        return await line.Locator("span.chip-ok").CountAsync() > 0
            ? GuestLineBadge.AtYourTable
            : GuestLineBadge.WithTheKitchen;
    }

    private static async Task<bool> WaitForCountAsync(
        IPage page,
        string selector,
        int expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(selector).CountAsync() == expected)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    private static async Task<bool> WaitForAttributeAsync(
        IPage page,
        string selector,
        string attribute,
        string expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        ILocator element = page.Locator(selector).First;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await element.CountAsync() > 0
                && string.Equals(
                    await element.GetAttributeAsync(attribute),
                    expected,
                    StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    private static async Task<string> DescribeBasketAsync(IPage page)
    {
        BasketContents basket = await ReadBasketAsync(page);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{basket.StagedAdds} staged add(s) and {basket.TickedRemovals} ticked removal(s)");
    }

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
        string? loaded = await surface.First.GetAttributeAsync("data-loaded");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"data-live='{live ?? "absent"}', data-loaded='{loaded ?? "absent"}'");
    }

    private const string CountingScript = """
        (selectors) => selectors.map((selector) => document.querySelectorAll(selector).length)
        """;
}
