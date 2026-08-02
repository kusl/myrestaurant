using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// The administration journeys the §16.3 scenarios walk: creating a table, issuing a display pairing
/// code, rotating a table's join secret, and putting something on the menu
/// (TECHNICAL_SPECIFICATION §4.1, §4.2, §7, §11.4).
///
/// <para>All of them go through the real static-SSR administration surfaces on a page that is signed in as
/// an administrator, because that is what the scenarios are about — "admin creates table" in §16.3 means
/// the form, the antiforgery token, the endpoint authorization and the redirect, not an
/// <c>INSERT</c>. The one place these scenarios do reach past the UI is reading a <c>join_secret</c>
/// (<see cref="RestaurantInstance.ReadJoinSecretAsync"/>), and only because §4.1 makes it deliberately
/// unreachable from every surface — which is the property under test rather than an obstacle to it.</para>
/// </summary>
/// <summary>
/// Something an administrator put on the menu (§7): the identifier the picker's <c>&lt;option&gt;</c>
/// carries, and the name every surface — the guest's basket, the kitchen ticket, the bill — reads.
/// </summary>
internal sealed record MenuItemOnTheMenu(Guid Identifier, string Name, decimal PriceAmount);

internal static class AdministrationJourneys
{
    private const string TablesPath = "/administration/tables";
    private const string MenuPath = "/administration/menu";

    /// <summary>
    /// Creates a table through <c>/administration/tables/new</c> (§4.1) and returns its identifier,
    /// taken from the "Manage this table" link on the success panel. Reading it back out of the page is
    /// deliberate: the identifier is minted server-side, so a scenario that recovers it this way is
    /// testing the surface rather than reimplementing it.
    /// </summary>
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

    /// <summary>
    /// Issues a one-time display pairing code from <c>/administration/tables/{table}/displays</c> (§4.2)
    /// and returns the plaintext.
    ///
    /// <para>The surface renders the code <em>in place</em> rather than through a redirect, precisely
    /// because this is the only moment the plaintext exists — only its SHA-256 hash is stored. So there
    /// is no post/redirect/get to wait on here, just the panel appearing.</para>
    /// </summary>
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

    /// <summary>
    /// Rotates a table's join secret from its management page (§4.1) and returns once the application has
    /// confirmed it.
    ///
    /// <para>Waiting for the confirmation is load-bearing rather than decorative. Rotation is a
    /// post/redirect/get, so the click returns as soon as the POST is issued; a scenario that read the new
    /// secret out of the database immediately afterwards could read the old one and then spend its
    /// remaining minute failing to explain why. The flash text is matched, not merely its presence,
    /// because a rename or an activation change flashes through the same element.</para>
    /// </summary>
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

    /// <summary>
    /// Puts an item on the menu through <c>/administration/menu/new</c> (§7) and returns it, identifier
    /// included — read back out of the "Manage this item" link the same way
    /// <see cref="CreateTableAsync"/> recovers a table's, because the identifier is minted server-side
    /// and a scenario that recovered it any other way would be reimplementing the surface.
    ///
    /// <para>The identifier is the part that matters downstream. The guest's picker renders one
    /// <c>&lt;option&gt;</c> per item whose <em>label</em> is the name, the price and possibly the words
    /// "currently unavailable" — so a scenario choosing by label would be matching on money formatting
    /// and §7's availability copy. The <c>value</c> is the bare identifier, and that is what
    /// <see cref="TableOrderJourneys"/> selects on.</para>
    /// </summary>
    internal static async Task<MenuItemOnTheMenu> CreateMenuItemAsync(IPage page, string name, decimal priceAmount)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{MenuPath}/new");

        await page.FillAsync("#name", name);
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

    private static string ManagePathFor(Guid tableIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{TablesPath}/{tableIdentifier:D}");

    private static string DisplaysPathFor(Guid tableIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{TablesPath}/{tableIdentifier:D}/displays");

    /// <summary>
    /// Whatever the surface has to say about why it did not do the thing. An administration page renders
    /// a refusal into <c>p.status-error</c>; a validation refusal lands in the form's validation summary;
    /// and being bounced somewhere else entirely (a lost session, a failed policy) shows up as the URL.
    /// </summary>
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
