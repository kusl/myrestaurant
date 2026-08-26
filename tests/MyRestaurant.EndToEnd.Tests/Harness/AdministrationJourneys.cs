using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record MenuItemOnTheMenu(Guid Identifier, string Name, decimal PriceAmount);

internal sealed record MenuHeadingOnTheIndex(
    string Name,
    bool IsVisibleToGuests,
    IReadOnlyList<string> ItemNames,
    bool OffersMoveUp,
    bool OffersMoveDown);

[Flags]
internal enum StaffRoles
{
    None = 0,

    Counter = 1,

    Kitchen = 2,

    Administrator = 4,
}

internal sealed record StaffAccount(
    Guid PersonIdentifier,
    string Username,
    string DisplayName,
    string TemporaryPassword);

internal sealed record CredentialReset(string TemporaryPassword, bool ClearedAuthenticator);

internal sealed record ManagedAccount(
    string Username,
    IReadOnlyList<string> StatusChips,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Credentials);

internal static class AdministrationJourneys
{
    private const string TablesPath = "/administration/tables";
    private const string MenuPath = "/administration/menu";
    private const string PeoplePath = "/administration/people";

    private const string MenuSectionsPath = MenuPath + "/sections";

    internal const string DefaultMenuSectionName = "E2E Section";

    private const string TemporaryPasswordSelector = "p.staff-temporary-password";

    internal static async Task<Guid> CreateTableAsync(IPage page, string label)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{TablesPath}/new");

        await page.FillAsync("#label", label);
        await page.ClickAsync("button:has-text('Create table')");

        ILocator manageLink = page.Locator("a:has-text('Manage this table')").First;

        try
        {
            await manageLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Creating the table '{label}' did not reach the success panel. "
                + await DescribeFailureAsync(page),
                exception);
        }

        string? href = await manageLink.GetAttributeAsync("href");
        string prefix = TablesPath + "/";

        if (href is null
            || !href.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(href[prefix.Length..], out Guid tableIdentifier))
        {
            throw new InvalidOperationException(
                $"The table-created panel linked to '{href}', which is not a table management URL.");
        }

        return tableIdentifier;
    }

    internal static async Task<string> IssuePairingCodeAsync(IPage page, Guid tableIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(DisplaysPathFor(tableIdentifier));
        await page.ClickAsync("button:has-text('Generate pairing code')");

        ILocator code = page.Locator("p.pairing-code").First;

        try
        {
            await code.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "Generating a display pairing code produced no code. " + await DescribeFailureAsync(page),
                exception);
        }

        string issued = (await code.InnerTextAsync()).Trim();

        if (issued.Length == 0)
        {
            throw new InvalidOperationException("The pairing-code panel rendered, but it is empty.");
        }

        return issued;
    }

    internal static async Task RotateJoinSecretAsync(IPage page, Guid tableIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(ManagePathFor(tableIdentifier));
        await page.ClickAsync("button:has-text('Rotate join secret')");

        ILocator confirmation = page.Locator("p.status-success").First;

        try
        {
            await confirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "Rotating the join secret was not confirmed. " + await DescribeFailureAsync(page),
                exception);
        }

        string message = (await confirmation.InnerTextAsync()).Trim();

        if (!message.Contains("Join secret rotated", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Rotating the join secret reported '{message}', which is some other outcome.");
        }
    }

    internal static async Task<Guid> CreateMenuSectionAsync(
        IPage page,
        string name,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{MenuSectionsPath}/new");

        await page.FillAsync("#name", name);

        await page.FillAsync("#description", description ?? string.Empty);

        await page.ClickAsync("button:has-text('Create section')");

        ILocator manageLink = page.Locator("a:has-text('Manage this section')").First;

        try
        {
            await manageLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Creating the menu section '{name}' did not reach the success panel. "
                + await DescribeFailureAsync(page),
                exception);
        }

        string? href = await manageLink.GetAttributeAsync("href");
        string prefix = MenuSectionsPath + "/";

        if (href is null
            || !href.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(href[prefix.Length..], out Guid menuSectionIdentifier))
        {
            throw new InvalidOperationException(
                $"The section-created panel linked to '{href}', which is not a menu section management URL.");
        }

        return menuSectionIdentifier;
    }

    internal static async Task SetMenuSectionVisibilityAsync(
        IPage page,
        Guid menuSectionIdentifier,
        bool visibleToGuests)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{MenuSectionsPath}/{menuSectionIdentifier:D}");

        string button = visibleToGuests ? "Show to guests" : "Hide from guests";
        string expectedChip = visibleToGuests ? "Can see this heading" : "Hidden from guests";

        ILocator control = page.Locator($"button:has-text('{button}')").First;

        if (await control.CountAsync() == 0)
        {
            return;
        }

        await control.ClickAsync();

        ILocator chip = page.Locator($".manage-facts .chip:has-text('{expectedChip}')").First;

        try
        {
            await chip.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Setting section {menuSectionIdentifier} to '{expectedChip}' did not take effect. "
                + await DescribeFailureAsync(page),
                exception);
        }
    }

    internal static async Task<Guid> EnsureMenuSectionAsync(IPage page, string name)
    {
        ArgumentNullException.ThrowIfNull(page);

        return await FindMenuSectionAsync(page, name)
            ?? await CreateMenuSectionAsync(page, name);
    }

    private static async Task<Guid?> FindMenuSectionAsync(IPage page, string name)
    {
        await page.GotoAsync($"{MenuPath}/new");

        ILocator options = page.Locator("#menu-section option");
        int count = await options.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator option = options.Nth(index);
            string label = (await option.InnerTextAsync()).Trim();

            if (label.EndsWith(InactiveSectionSuffix, StringComparison.Ordinal))
            {
                label = label[..^InactiveSectionSuffix.Length].TrimEnd();
            }

            if (!string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? value = await option.GetAttributeAsync("value");

            if (!Guid.TryParse(value, out Guid identifier))
            {
                throw new InvalidOperationException(
                    $"The section option for '{name}' carries the value '{value}', which is not an"
                    + " identifier. §16.3 chooses a section by identifier because a label is copy.");
            }

            return identifier;
        }

        return null;
    }

    private const string InactiveSectionSuffix = "(hidden from guests)";

    internal static async Task<MenuItemOnTheMenu> CreateMenuItemAsync(
        IPage page,
        string name,
        decimal priceAmount,
        string? description = null,
        string sectionName = DefaultMenuSectionName)
    {
        ArgumentNullException.ThrowIfNull(page);

        Guid sectionIdentifier = await EnsureMenuSectionAsync(page, sectionName);

        await page.GotoAsync($"{MenuPath}/new");

        await page.SelectOptionAsync("#menu-section", sectionIdentifier.ToString("D"));

        await page.FillAsync("#name", name);

        await page.FillAsync("#description", description ?? string.Empty);

        await page.FillAsync("#price", priceAmount.ToString("0.00", CultureInfo.InvariantCulture));
        await page.ClickAsync("button:has-text('Create item')");

        ILocator manageLink = page.Locator("a:has-text('Manage this item')").First;

        try
        {
            await manageLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Creating the menu item '{name}' did not reach the success panel. "
                + await DescribeFailureAsync(page),
                exception);
        }

        string? href = await manageLink.GetAttributeAsync("href");
        string prefix = MenuPath + "/";

        if (href is null
            || !href.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(href[prefix.Length..], out Guid menuItemIdentifier))
        {
            throw new InvalidOperationException(
                $"The item-created panel linked to '{href}', which is not a menu item management URL.");
        }

        return new MenuItemOnTheMenu(menuItemIdentifier, name, priceAmount);
    }

    internal static async Task MoveMenuItemToSectionAsync(
        IPage page,
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{MenuPath}/{menuItemIdentifier:D}");

        await page.SelectOptionAsync("#move-section", menuSectionIdentifier.ToString("D"));
        await page.ClickAsync("button:has-text('File here')");

        ILocator sectionLink = page
            .Locator($".manage-facts a[href='{MenuSectionsPath}/{menuSectionIdentifier:D}']")
            .First;

        try
        {
            await sectionLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Filing menu item {menuItemIdentifier} under section {menuSectionIdentifier} did not"
                + " take effect. "
                + await DescribeFailureAsync(page),
                exception);
        }
    }

    internal static async Task MoveMenuHeadingAsync(IPage page, Guid menuSectionIdentifier, bool up)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(MenuPath);

        ILocator group = page
            .Locator(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"details.menu-group:has(div.menu-group-actions"
                    + $" a[href='{MenuSectionsPath}/{menuSectionIdentifier:D}'])"))
            .First;

        try
        {
            await group.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The menu index renders no group for heading {menuSectionIdentifier:D}. ")
                + await DescribeFailureAsync(page),
                exception);
        }

        string label = up ? "Move up" : "Move down";

        await PressMoveAsync(
            page,
            group.Locator($"div.menu-group-actions button:has-text('{label}')").First,
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{label}' on heading {menuSectionIdentifier:D}"));
    }

    internal static async Task MoveMenuItemAsync(IPage page, Guid menuItemIdentifier, bool up)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(MenuPath);

        ILocator row = page
            .Locator(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"div.menu-group-body tr:has(a.record-link[href$='/{menuItemIdentifier:D}'])"))
            .First;

        try
        {
            await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The menu index has no row for item {menuItemIdentifier:D}. ")
                + await DescribeFailureAsync(page),
                exception);
        }

        string label = up ? "Up" : "Down";

        await PressMoveAsync(
            page,
            row.Locator($"td.record-actions button:has-text('{label}')").First,
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{label}' on item {menuItemIdentifier:D}"));
    }

    private static async Task PressMoveAsync(IPage page, ILocator control, string what)
    {
        if (await control.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The menu index offers no {what}. ")
                + await DescribeFailureAsync(page));
        }

        if (await control.IsDisabledAsync())
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{what} is disabled, which §11.4 means: it is at that end of its list already and"
                    + $" would exchange with nothing. The control is rendered rather than omitted on"
                    + $" purpose, so this is the surface behaving correctly and the caller asking for a"
                    + $" move that does not exist."));
        }

        await control.ClickAsync();

        ILocator confirmation = page.Locator("p.status-success").First;

        try
        {
            await confirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Pressing {what} was not confirmed. ")
                + await DescribeFailureAsync(page),
                exception);
        }

        string message = await ScreenText.DeclaredAsync(confirmation);

        if (!message.Contains("Moved.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pressing {what} reported '{message}', which is one of §11.4's two outcomes that"
                    + $" wrote nothing — the position was already that one, or the set changed while the"
                    + $" page was open. Neither is a move, and neither will produce the order the caller"
                    + $" is about to wait for."));
        }
    }

    internal static async Task<int?> ReadMenuIndexLikeCountAsync(IPage page, Guid menuItemIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(MenuPath);

        ILocator row = page
            .Locator($"div.menu-group-body tr:has(a.record-link[href*='{menuItemIdentifier:D}'])")
            .First;

        try
        {
            await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The menu index has no row for item {menuItemIdentifier:D}. ")
                + await DescribeFailureAsync(page),
                exception);
        }

        ILocator chip = row.Locator("td.record-primary span.chip[data-like-count]");

        if (await chip.CountAsync() == 0)
        {
            return null;
        }

        string? declared = await chip.First.GetAttributeAsync("data-like-count");

        return int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out int likes)
            ? likes
            : throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§11.4's like chip carries data-like-count=\"{declared}\", which is not an integer."));
    }

    internal static async Task<IReadOnlyList<MenuHeadingOnTheIndex>> ReadMenuIndexAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(MenuPath);

        ILocator wrapper = page.Locator("div.menu-groups").First;

        try
        {
            await wrapper.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "The menu index rendered no headings at all, so it is either still loading or showing the"
                + " first-use panel for a menu with no sections. "
                + await DescribeFailureAsync(page),
                exception);
        }

        ILocator groups = page.Locator("div.menu-groups > details.menu-group");
        int count = await groups.CountAsync();

        List<MenuHeadingOnTheIndex> headings = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator group = groups.Nth(index);

            string name = await ScreenText.DeclaredAsync(
                group.Locator("summary.menu-group-summary span.menu-group-name").First);

            bool visible = await group
                .Locator("summary.menu-group-summary span.chip-ok")
                .CountAsync() > 0;

            IReadOnlyList<string> raw = await group
                .Locator("div.menu-group-body td.record-primary a.record-link")
                .AllTextContentsAsync();

            ILocator moves = group.Locator("div.menu-group-actions button");
            int moveCount = await moves.CountAsync();

            if (moveCount != 2)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The '{name}' group renders {moveCount} move control(s) and §11.4 renders two —"
                        + $" Up and Down, the edge one disabled rather than omitted, because a control"
                        + $" that vanishes at the end of a list moves every other control up a row on the"
                        + $" next render."));
            }

            headings.Add(new MenuHeadingOnTheIndex(
                name,
                visible,
                [.. raw.Select(ScreenText.Collapse)],
                !await moves.Nth(0).IsDisabledAsync(),
                !await moves.Nth(1).IsDisabledAsync()));
        }

        return headings;
    }

    internal static async Task<StaffAccount> CreateStaffAccountAsync(
        IPage page,
        string username,
        string displayName,
        StaffRoles roles)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{PeoplePath}/new");

        await page.FillAsync("#username", username);
        await page.FillAsync("#display-name", displayName);

        if (roles.HasFlag(StaffRoles.Counter))
        {
            await TickRoleAsync(page, "Counter");
        }

        if (roles.HasFlag(StaffRoles.Kitchen))
        {
            await TickRoleAsync(page, "Kitchen");
        }

        if (roles.HasFlag(StaffRoles.Administrator))
        {
            await TickRoleAsync(page, "Administrator");
        }

        await page.ClickAsync("button:has-text('Create account')");

        ILocator temporaryPassword = page.Locator(TemporaryPasswordSelector).First;

        try
        {
            await temporaryPassword.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Creating the staff account '{username}' did not reach the success panel, so there is"
                + " no temporary password to hand over — and there will never be another chance to read"
                + " one, because the plaintext is written into that response and nowhere else. "
                + await DescribeFailureAsync(page),
                exception);
        }

        string issued = (await temporaryPassword.InnerTextAsync()).Trim();

        if (issued.Length == 0)
        {
            throw new InvalidOperationException(
                $"The staff-account panel for '{username}' rendered its temporary password element empty.");
        }

        ILocator manageLink = page.Locator("a:has-text('Manage this account')").First;
        string? href = await manageLink.GetAttributeAsync("href");
        string prefix = PeoplePath + "/";

        if (href is null
            || !href.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(href[prefix.Length..], out Guid personIdentifier))
        {
            throw new InvalidOperationException(
                $"The staff-account panel linked to '{href}', which is not a person management URL.");
        }

        return new StaffAccount(personIdentifier, username, displayName, issued);
    }

    internal static async Task<CredentialReset> ResetCredentialsAsync(IPage page, Guid personIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(ManagePersonPathFor(personIdentifier));
        await page.ClickAsync("button:has-text('Reset credentials')");

        ILocator temporaryPassword = page.Locator(TemporaryPasswordSelector).First;

        try
        {
            await temporaryPassword.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "Resetting the account's credentials did not reach the panel that shows the temporary"
                + " password, so there is nothing to hand over — and there will never be another chance"
                + " to read one, because the plaintext is written into that response and nowhere else. "
                + await DescribeFailureAsync(page),
                exception);
        }

        string issued = (await temporaryPassword.InnerTextAsync()).Trim();

        if (issued.Length == 0)
        {
            throw new InvalidOperationException(
                "The credentials-reset panel rendered its temporary password element empty.");
        }

        string sentence = await ScreenText.DeclaredAsync(page.Locator("p.status-success").First);

        bool clearedAuthenticator =
            sentence.Contains("the authenticator was cleared", StringComparison.Ordinal);

        if (!clearedAuthenticator && !sentence.Contains("The password was reset", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The credentials-reset panel reported '{sentence}', which is neither of the two outcomes"
                + " §3.7 defines for a reset.");
        }

        return new CredentialReset(issued, clearedAuthenticator);
    }

    internal static async Task<ManagedAccount> ReadAccountFactsAsync(IPage page, Guid personIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(ManagePersonPathFor(personIdentifier));

        ILocator facts = page.Locator("div.manage-facts").First;

        try
        {
            await facts.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"The management page for {personIdentifier:D} rendered no facts panel, so it is either"
                + " reporting that no such account exists or showing some other panel entirely. "
                + await DescribeFailureAsync(page),
                exception);
        }

        string username = (await page.Locator("section.account-panel h1").First.InnerTextAsync()).Trim();

        ILocator groups = page.Locator("div.manage-facts > div");
        int count = await groups.CountAsync();

        Dictionary<string, IReadOnlyList<string>> byLabel = new(StringComparer.Ordinal);

        for (int index = 0; index < count; index++)
        {
            ILocator group = groups.Nth(index);
            string label = await ScreenText.DeclaredAsync(group.Locator("span.manage-label").First);

            IReadOnlyList<string> raw = await group.Locator("span.chip").AllTextContentsAsync();
            byLabel[label] = [.. raw.Select(ScreenText.Collapse)];
        }

        return new ManagedAccount(
            username,
            ChipsUnder(byLabel, "Status", personIdentifier),
            ChipsUnder(byLabel, "Roles", personIdentifier),
            ChipsUnder(byLabel, "Credentials", personIdentifier));
    }

    private static IReadOnlyList<string> ChipsUnder(
        Dictionary<string, IReadOnlyList<string>> byLabel,
        string label,
        Guid personIdentifier)
    {
        if (byLabel.TryGetValue(label, out IReadOnlyList<string>? chips))
        {
            return chips;
        }

        string offered = byLabel.Count == 0
            ? "nothing at all"
            : string.Join("; ", byLabel.Keys.Select(key => $"'{key}'"));

        throw new InvalidOperationException(
            $"The management page for {personIdentifier:D} has no '{label}' group of facts. What it"
            + $" offers: {offered}.");
    }

    private static async Task TickRoleAsync(IPage page, string roleName)
    {
        ILocator choices = page.Locator("fieldset.choice-fieldset label.choice");
        int count = await choices.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator choice = choices.Nth(index);
            string name = (await choice.Locator("span.choice-name").First.InnerTextAsync()).Trim();

            if (!string.Equals(name, roleName, StringComparison.Ordinal))
            {
                continue;
            }

            await choice.Locator("input[type='checkbox']").First.CheckAsync();
            return;
        }

        string offered = await DescribeRoleChoicesAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The create-staff form offers no '{roleName}' role to grant. What it offers: {offered}."));
    }

    private static async Task<string> DescribeRoleChoicesAsync(IPage page)
    {
        ILocator names = page.Locator("fieldset.choice-fieldset label.choice span.choice-name");

        if (await names.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"nothing at all; the browser is at '{page.Url}'");
        }

        IReadOnlyList<string> all = await names.AllInnerTextsAsync();

        return string.Join("; ", all.Select(text => $"'{text.Trim()}'"));
    }

    private static string ManagePathFor(Guid tableIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{TablesPath}/{tableIdentifier:D}");

    private static string ManagePersonPathFor(Guid personIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{PeoplePath}/{personIdentifier:D}");

    private static string DisplaysPathFor(Guid tableIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{TablesPath}/{tableIdentifier:D}/displays");

    private static async Task<string> DescribeFailureAsync(IPage page)
    {
        ILocator errors = page.Locator("p.status-error, .validation-message");

        if (await errors.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The browser is at '{page.Url}' and the page reports no error.");
        }

        string message = (await errors.First.InnerTextAsync()).Trim();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The page reports: {message} (browser at '{page.Url}').");
    }
}
