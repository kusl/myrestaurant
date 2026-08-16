using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// The administration journeys the §16.3 scenarios walk: creating a table, issuing a display pairing
/// code, rotating a table's join secret, putting something on the menu, and — on the people side —
/// creating a staff account, reading one's facts back, and resetting its credentials
/// (TECHNICAL_SPECIFICATION §3.7, §4.1, §4.2, §7, §11.4).
///
/// <para>All of them go through the real static-SSR administration surfaces on a page that is signed in as
/// an administrator, because that is what the scenarios are about — "admin creates table" in §16.3 means
/// the form, the antiforgery token, the endpoint authorization and the redirect, not an
/// <c>INSERT</c>. The one place these scenarios do reach past the UI is reading a <c>join_secret</c>
/// (<see cref="RestaurantInstance.ReadJoinSecretAsync"/>), and only because §4.1 makes it deliberately
/// unreachable from every surface — which is the property under test rather than an obstacle to it.</para>
/// </summary>
/// <summary>
/// Something an administrator put on the menu (§7): the identifier the guest picker's card carries in
/// <c>data-menu-item</c>, and the name every surface — the guest's basket, the kitchen ticket, the bill —
/// reads.
///
/// <para>The description is deliberately <b>not</b> a member. What was typed into the form is the
/// arrangement; what the guest surface shows is the assertion, and <c>MenuCard</c> is where a scenario
/// reads it. Carrying it here would invite a scenario to compare the surface against this record instead
/// of against the sentence it passed in, which is a test comparing a value to itself.</para>
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

/// <summary>
/// What an administrative credential reset (§3.7) handed back: the temporary password the panel showed
/// once, and whether the account had an authenticator that was cleared with it.
///
/// <para><see cref="ClearedAuthenticator"/> is read off the panel's own sentence rather than inferred.
/// §3.7 makes the TOTP half <em>conditional</em> — <c>ResetCredentialsAsync</c> probes
/// <c>totp_secret_protected</c> and only then nulls it, deletes the recovery codes, sets
/// <c>must_enroll_totp</c> and records <c>totp_cleared_by_administrator</c> — so the flag is the
/// difference between the reset §16.3 scenario 12 is about and a password-only one that would leave the
/// scenario's second obligation permanently unreachable. A caller asserting on it is asserting that the
/// account really was enrolled at the moment the administrator pressed the button.</para>
///
/// <para>Like <see cref="StaffAccount.TemporaryPassword"/>, the password here exists in exactly one HTTP
/// response and nowhere else — <c>ManagePerson.razor</c> generates it, hashes it, and renders the
/// plaintext without a redirect precisely so there is no second chance to read it.</para>
/// </summary>
internal sealed record CredentialReset(string TemporaryPassword, bool ClearedAuthenticator);

/// <summary>
/// One account as §3.7's management surface describes it: the chips under Status, the roles it holds, and
/// the credentials it carries.
///
/// <para><b>Chips rather than columns, deliberately.</b> Every fact here has a row in <c>person</c> that
/// a fixture could read directly, and reading it directly would prove nothing about §3.7: that
/// <c>must_change_password</c> is set is one claim, and that an administrator can <em>see</em> it is
/// another, and only the second is a product behaviour. The same argument applies in reverse to
/// <see cref="Credentials"/>, which is derived rather than stored — "Authenticator" appears iff
/// <c>totp_secret_protected IS NOT NULL</c> (§3.4 has no enrolled column), so the absence of that chip
/// after a reset is the surface agreeing that the secret is gone.</para>
///
/// <para>An empty list means the surface said "None" in prose rather than in a chip: no role at all
/// ("None (guest)"), or no credentials. Both are rendered as a <c>span.muted</c>, which is not a chip
/// and is not collected — so <c>Roles.Count == 0</c> reads as "a guest" and not as "the reader
/// missed them".</para>
/// </summary>
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

    /// <summary>
    /// The heading <see cref="CreateMenuItemAsync"/> files an item under when a scenario does not say.
    ///
    /// <para><b>A default exists so that <c>0005</c> reached one file instead of sixteen.</b> §7 makes an
    /// item's section mandatory, and six of the §16.3 scenarios put something on the menu without caring
    /// what it is filed under — they are about ordering, settlement and reachability. Threading a section
    /// through every one of them would be sixteen edits to say a thing none of them means. So the journey
    /// arranges a heading on their behalf, exactly as it has always arranged the form's antiforgery token
    /// and its redirect.</para>
    ///
    /// <para>Named for what it is rather than something plausible like "Mains": a scenario reading this
    /// word off a surface should not be able to mistake it for a decision the scenario made.</para>
    /// </summary>
    internal const string DefaultMenuSectionName = "E2E Section";

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
    /// Puts a heading on the menu through <c>/administration/menu/sections/new</c> (§7, §11.4) and
    /// returns its identifier, taken from the "Manage this section" link on the success panel.
    ///
    /// <para><b>This journey read the identifier off the item form's <c>&lt;option value&gt;</c> for one
    /// slice, and the shape it has now is the one it was always going to have.</b> Slice 40 shipped the
    /// create page without an editor, so a section's success panel had no management page to link to and
    /// the only place its identifier appeared anywhere was the picker on a different form. That was
    /// reading the surface rather than reaching past it — which is what §16.3 asks for — and it was
    /// recorded at the time as a shape that goes away when the editor exists. It does, here.</para>
    ///
    /// <para>Recovering it from a "Manage this…" link is what <see cref="CreateTableAsync"/>,
    /// <see cref="CreateMenuItemAsync"/> and <see cref="CreateStaffAccountAsync"/> all do, and the reason
    /// is the same for all four: the identifier is minted server-side, so a scenario that recovered it any
    /// other way would be reimplementing the surface.</para>
    ///
    /// <para>Throws when the name is already taken. <see cref="EnsureMenuSectionAsync"/> is the idempotent
    /// one — a scenario that means "create this" wants to hear that it did not.</para>
    /// </summary>
    internal static async Task<Guid> CreateMenuSectionAsync(
        IPage page,
        string name,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync($"{MenuSectionsPath}/new");

        await page.FillAsync("#name", name);

        // Filled unconditionally, including with the empty string, for the reason CreateMenuItemAsync
        // fills the description that way: a form reached twice in one scenario keeps what was typed the
        // first time, so skipping the fill would silently attach the previous section's description.
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

    /// <summary>
    /// Switches a heading on or off from <c>/administration/menu/sections/{id}</c> (§7) and returns once
    /// the surface agrees it moved.
    ///
    /// <para><b>Through the editor, because the rule under test is not a database rule.</b> §7's asymmetry
    /// — an inactive <em>section</em> is hidden from the guest entirely, where an inactive <em>item</em>
    /// stays visible and marked — is asserted at the data layer by <c>MenuDirectoryTests</c>, and what no
    /// unit test can see is whether the guest's menu actually loses the heading. That needs a real flip
    /// through a real form, which is what this is; the assertion cut from scenario 17 in Slice 40 was cut
    /// precisely because this journey could not exist yet.</para>
    ///
    /// <para>The wait is on the chip rather than on the flash, because the flash is copy and the chip is
    /// the fact — and because a no-op flip redirects without one, which is a state this method should
    /// report as success rather than time out on.</para>
    /// </summary>
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
            // Already in the requested state: the page renders one of the two forms, never both. Nothing
            // to press and nothing wrong, which is the same reading the surface itself takes.
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

    /// <summary>
    /// Makes sure a heading with this name exists, and returns its identifier — creating it when it does
    /// not and finding it when it does.
    ///
    /// <para><b>Idempotent by looking first rather than by swallowing a failure.</b> The alternative —
    /// submit, and treat "that name is taken" as success — would also pass on a form that reported the
    /// wrong error, and §7's names are <c>citext</c>-unique, so "taken" is a real outcome this project
    /// asserts elsewhere rather than something to catch and discard.</para>
    /// </summary>
    internal static async Task<Guid> EnsureMenuSectionAsync(IPage page, string name)
    {
        ArgumentNullException.ThrowIfNull(page);

        return await FindMenuSectionAsync(page, name)
            ?? await CreateMenuSectionAsync(page, name);
    }

    /// <summary>
    /// The identifier of the section with this name, or <c>null</c> when the item form offers none —
    /// which is also the answer on a fresh instance, where that form renders its "give the menu a
    /// heading first" panel and has no picker at all.
    ///
    /// <para>This is a <em>lookup</em> and stays one. <see cref="CreateMenuSectionAsync"/> used to borrow
    /// it to recover the identifier of a section it had just made, because a section had no management
    /// page to link to; it now reads its own success panel like every other create journey here, which
    /// leaves this method doing the one thing it was written for — answering "does a heading with this
    /// name already exist" for <see cref="EnsureMenuSectionAsync"/>.</para>
    ///
    /// <para>Matched on the option's text with the surface's own inactive suffix allowed for, and
    /// compared case-insensitively because <c>menu_section.name</c> is <c>citext</c>: "drinks" and
    /// "Drinks" are one heading, so a harness that treated them as two would arrange a duplicate the
    /// database is about to refuse.</para>
    /// </summary>
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

    /// <summary>
    /// What <c>CreateMenuItem.razor</c> appends to an inactive section's option label (§7 — an inactive
    /// heading is hidden from the guest and offered to the administrator). Declared once here because two
    /// methods above strip it, and a second spelling of it would make one of them silently stop matching.
    /// </summary>
    private const string InactiveSectionSuffix = "(hidden from guests)";

    /// <summary>
    /// Puts an item on the menu through <c>/administration/menu/new</c> (§7) and returns it, identifier
    /// included — read back out of the "Manage this item" link the same way
    /// <see cref="CreateTableAsync"/> recovers a table's, because the identifier is minted server-side
    /// and a scenario that recovered it any other way would be reimplementing the surface.
    ///
    /// <para>The identifier is the part that matters downstream. The guest's picker renders one card per
    /// item whose visible text is the name, the <em>formatted</em> price and, for a deactivated item, §7's
    /// availability chip — so a scenario choosing by what it can read would be matching on money formatting
    /// and on availability copy. The card carries the bare identifier in <c>data-menu-item</c>, and that is
    /// what <see cref="TableOrderJourneys.ChooseAsync"/> clicks. Until M6 Slice 39 the picker was a
    /// <c>&lt;select&gt;</c> and the identifier was an <c>&lt;option&gt;</c>'s <c>value</c>; the shape
    /// changed and this reasoning did not.</para>
    ///
    /// <para><b>The description is optional here because it is optional in §7</b>, and passing it is how a
    /// scenario arranges an item that has something for the guest surface to show. A blank one stores
    /// <c>""</c> and writes no <c>description_changed</c> event at all, which is the no-op rule rather than
    /// a special case — so "created without a description" is a real arrangement and not merely the absence
    /// of one.</para>
    ///
    /// <para><b>The section is arranged before the form is opened, and that is what <c>0005</c> costs a
    /// caller.</b> §7 requires every item to be under a heading, and the create form renders a first-use
    /// panel instead of a form when there are none — so a journey that went straight to
    /// <c>/administration/menu/new</c> on a fresh instance would find no <c>#name</c> field and fail with
    /// a timeout naming the wrong thing. <see cref="EnsureMenuSectionAsync"/> runs first, tolerating a
    /// name already taken, so this stays callable any number of times in one scenario.</para>
    /// </summary>
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

        // Selected by VALUE rather than by label, because the label an inactive section renders carries
        // §7's "(hidden from guests)" suffix — so matching on the visible text would make this journey
        // depend on a surface's copy, which is the same mistake choosing a guest menu item by its
        // formatted price would be.
        await page.SelectOptionAsync("#menu-section", sectionIdentifier.ToString("D"));

        await page.FillAsync("#name", name);

        // Filled unconditionally, including with the empty string, for the reason TableOrderJourneys fills
        // the customization note that way: a form reached twice in one scenario keeps what was typed the
        // first time, so skipping the fill would silently attach the previous item's description to this
        // one.
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
    /// Resets one account's credentials from its management page (§3.7) and returns the temporary
    /// password the panel showed, together with whether an enrolled authenticator was cleared with it.
    ///
    /// <para><b>No redirect to wait on, and that is the design rather than an omission.</b> Every other
    /// action on this page is a post/redirect/get carrying a one-word outcome, so a refresh cannot
    /// re-post it. This one renders in place, because the plaintext password exists only in the response
    /// that generated it — a redirect would either lose it or park it in a query string. So the barrier
    /// below is the panel appearing, not a URL changing.</para>
    ///
    /// <para><b>The outcome sentence is matched, not merely found.</b> §3.7 writes one of two, and the
    /// difference is exactly whether <c>must_enroll_totp</c> was set — which decides whether §3.5's
    /// second obligation exists at all. A caller that assumed the authenticator branch and got the
    /// password-only one would go on to wait out a timeout on a page no principal was ever going to be
    /// sent to.</para>
    /// </summary>
    internal static async Task<CredentialReset> ResetCredentialsAsync(IPage page, Guid personIdentifier)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(ManagePersonPathFor(personIdentifier));
        await page.ClickAsync("button:has-text('Reset credentials')");

        // p.staff-temporary-password rather than p.totp-secret, which the element also carries for its
        // monospaced treatment. The distinction is load-bearing here in a way it was only prudent on the
        // create-staff panel: the very next surface this account sees is §3.5's re-enrollment page, whose
        // own p.totp-secret holds a real authenticator key, so the narrower name is what keeps "read the
        // secret off the screen" from meaning two different secrets in one scenario.
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

        // Both sentences open the same way; only one of them mentions the authenticator. Matched on the
        // clause rather than on the whole sentence so a copy edit elsewhere in it is not a test failure.
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

    /// <summary>
    /// Reads one account's facts off its management page (§3.7): the Status chips, the roles, and the
    /// credentials.
    ///
    /// <para><b>Groups are found by the label beside them, never by position</b> — the same reasoning as
    /// <see cref="TickRoleAsync"/>. The three <c>div</c>s inside <c>.manage-facts</c> carry no ids, and
    /// indexing into them would work today and silently start reading roles as credentials the day a
    /// fourth fact is added above an existing one, which is precisely the kind of failure a scenario would
    /// blame on the application.</para>
    ///
    /// <para><b>Declared text rather than rendered text, for two independent reasons.</b>
    /// <c>.manage-label</c> is upcased for the eyebrow treatment, so the label this method matches on
    /// reads back as <c>STATUS</c> through <c>InnerTextAsync</c> and the lookup would miss every time.
    /// And <c>.chip-role</c> is capitalized, so a role chip whose markup says <c>kitchen</c> — the stored
    /// vocabulary, which is what <c>person_role.role_name</c>'s CHECK constrains and what a caller will
    /// want to compare against — would read back as <c>Kitchen</c>. See <see cref="ScreenText"/>; this is
    /// the second site in the harness where a stylesheet was in a position to fail a correct assertion.</para>
    ///
    /// <para>Waiting on <c>.manage-facts</c> is also what tells this page apart from the two other things
    /// the same route renders: the not-found panel, and the credentials-reset panel a caller might still
    /// be looking at from <see cref="ResetCredentialsAsync"/>. Neither has one.</para>
    /// </summary>
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

    /// <summary>
    /// One fact group's chips, or a sentence naming every group the page did offer.
    ///
    /// <para>A missing group is a failure rather than an empty list. An empty list already means
    /// something specific on this page — the surface said "None" in prose — and letting a group that was
    /// never rendered collapse into the same value would turn a renamed heading into a silently passing
    /// assertion about an account having no roles.</para>
    /// </summary>
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

    /// <summary>
    /// §3.7's per-account management route. Built from <see cref="PeoplePath"/> and formatted <c>D</c>,
    /// which is what <c>CreateStaffAccountAsync</c> parsed the identifier out of, so a round trip through
    /// this and back is exact.
    /// </summary>
    private static string ManagePersonPathFor(Guid personIdentifier)
        => string.Create(CultureInfo.InvariantCulture, $"{PeoplePath}/{personIdentifier:D}");

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
