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

/// <summary>
/// The roles §3.7's create-staff form offers, as the flags an administrator ticks. A flags enum rather
/// than three booleans at every call site, because "counter only" and "counter and kitchen" are the two
/// interesting shapes and a scenario should be able to say which it means.
///
/// <para><c>guest</c> is deliberately absent, and so is <c>table_display</c>: the first is the implicit
/// capacity of any authenticated person on their own order and the second is a device principal —
/// neither is a stored role, which is exactly what <c>RestaurantRoles</c> says.</para>
/// </summary>
[Flags]
internal enum StaffRoles
{
    /// <summary>No role at all. §3.7: such an account "behaves like a guest until a role is granted".</summary>
    None = 0,

    /// <summary>Close and settle sittings, adjust prices, show a table's join QR.</summary>
    Counter = 1,

    /// <summary>Fulfil lines, edit orders, turn menu items off and on.</summary>
    Kitchen = 2,

    /// <summary>Everything, including the administration area itself.</summary>
    Administrator = 4,
}

/// <summary>
/// A staff account an administrator created (§3.7), with the temporary password the surface showed once.
///
/// <para><b>The temporary password is the interesting field, and it is fragile by design.</b>
/// <c>CreateStaff.razor</c> generates it, hashes it immediately, and writes the plaintext into exactly
/// one HTTP response — there is no second chance to read it and nothing in the database that could
/// answer the question later. So the journey that creates the account is also the only place that can
/// capture it.</para>
///
/// <para>It is not the password the account ends up with. The row is written with
/// <c>must_change_password</c>, so §3.5's pipeline forces a real one before the account can reach any
/// authenticated endpoint — see <see cref="AccountJourneys.SignInWithPasswordAsync"/> and
/// <see cref="AccountJourneys.CompleteForcedPasswordChangeAsync"/>. Those are two calls rather than one
/// because the page a staff member lands on in between is itself worth asserting on: a §3.7 account that
/// walked straight past the obligation would be a hole rather than a convenience.</para>
/// </summary>
internal sealed record StaffAccount(
    Guid PersonIdentifier,
    string Username,
    string DisplayName,
    string TemporaryPassword);

internal static class AdministrationJourneys
{
    private const string TablesPath = "/administration/tables";
    private const string MenuPath = "/administration/menu";
    private const string PeoplePath = "/administration/people";

    /// <summary>
    /// The element that carries the one-time temporary password on the create-staff success panel. A
    /// class of its own as of M6 Slice 12: the element also carries <c>.totp-secret</c>, which it
    /// borrowed for the monospaced treatment, and reading a password out of something named for a TOTP
    /// secret is a dependency that breaks silently the day that page grows a real authenticator panel.
    /// </summary>
    private const string TemporaryPasswordSelector = "p.staff-temporary-password";

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

    /// <summary>
    /// Creates a staff account through <c>/administration/people/new</c> (§3.7) and returns it, including
    /// the temporary password the success panel showed — the only moment that plaintext exists anywhere.
    ///
    /// <para><b>Through the form, for the reason §16.3 keeps insisting on.</b> "Admin creates staff
    /// account" is the antiforgery token, the administrator-only endpoint authorization, the duplicate
    /// username check, the generated password, the Argon2id hash, the role grants recording <em>this</em>
    /// administrator as grantor, and the <c>must_change_password</c> flag — all in one transaction. An
    /// <c>INSERT</c> would arrange an account with none of that and would then prove nothing about the
    /// forced-change journey the caller is about to walk.</para>
    ///
    /// <para><b>Roles are ticked by name rather than by position.</b> The three checkboxes are
    /// <c>InputCheckbox</c> components inside <c>label.choice</c> elements and carry no id, so the row is
    /// found by the <c>span.choice-name</c> beside it. Indexing into the list would work today and would
    /// silently grant the wrong role the day a fourth role is added above an existing one — which is
    /// exactly the kind of failure a scenario would blame on authorization.</para>
    ///
    /// <para>The identifier comes back off the "Manage this account" link, the same way
    /// <see cref="CreateTableAsync"/> and <see cref="CreateMenuItemAsync"/> recover theirs.</para>
    /// </summary>
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

    /// <summary>
    /// Ticks one role checkbox on the create-staff form, found by the name rendered beside it.
    ///
    /// <para><c>CheckAsync</c> rather than <c>ClickAsync</c>: it is a no-op on a box that is already
    /// ticked, where a click would untick it. The form is static SSR so nothing is bound live — the
    /// checkbox state at submit is the whole of what is read — but a helper that quietly meant "toggle"
    /// would be a trap for the first caller who asked for the same role twice.</para>
    /// </summary>
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

        // Read before composing: an await inside an interpolation hole of a string that binds to
        // DefaultInterpolatedStringHandler is CS4007, because the handler is a ref struct.
        string offered = await DescribeRoleChoicesAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The create-staff form offers no '{roleName}' role to grant. What it offers: {offered}."));
    }

    /// <summary>The role names the form is offering, for a failure message.</summary>
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
