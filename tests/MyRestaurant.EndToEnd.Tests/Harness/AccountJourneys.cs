using Microsoft.Playwright;
using MyRestaurant.Domain.Security;
using MyRestaurant.WebApplication.Identity;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>The account a scenario bootstraps at <c>/setup</c> (§3.6).</summary>
internal sealed record AdministratorAccount(string Username, string DisplayName, string Password);

/// <summary>
/// A guest a scenario self-registers at <c>/register</c> (§4.3). No password field: the journey that
/// consumes this registers a passkey and nothing else, which is the passkey-first default and the
/// shape that proves <c>person.password_hash</c> is genuinely optional.
/// </summary>
internal sealed record GuestAccount(string Username, string DisplayName);

/// <summary>
/// The account journeys more than one §16.3 scenario walks: the first-administrator wizard, guest
/// registration, sign-out, both sign-in paths, both halves of §3.5's obligations pipeline, and the two
/// voluntary credential surfaces (§3.3's passkeys, §3.4's authenticator). Kept out of the scenario file
/// so a scenario reads as its own assertion rather than as thirty lines of form filling — and so that
/// when a surface changes, one place changes.
///
/// <para>The pipeline's two obligations sit next to each other here on purpose. They are one mechanism
/// with a fixed order — a reset wipes the password and, if one was enrolled, the authenticator, so the
/// password is re-established first and the authenticator second — and a harness that split them across
/// two files would make that order look like a coincidence of call sites.</para>
/// </summary>
internal static class AccountJourneys
{
    /// <summary>
    /// Ten single-use recovery codes are minted by the §3.6 bootstrap — and by every other set this
    /// application issues, because they all come from the Domain's <c>RecoveryCode.GenerateSet</c> and
    /// <c>RecoveryCode.CodesPerSet</c> is ten. So this is the expected size of a voluntary enrollment's
    /// set (§3.4) and of a forced re-enrollment's (§3.5) as much as of the wizard's.
    /// </summary>
    internal const int ExpectedRecoveryCodeCount = 10;

    /// <summary>
    /// An element the landing page has and no account surface does, for use as an arrival barrier when
    /// something in this file follows a link home (see <see cref="EnhancedNavigation"/>).
    ///
    /// <para><c>Home.razor</c> is the only page in the application whose <c>h1</c> carries a class. Every
    /// account panel renders a bare one, so this cannot be satisfied by the page being left — which is
    /// the single property an arrival selector has to have.</para>
    /// </summary>
    internal const string LandingPageMarker = "h1.landing-title";

    /// <summary>
    /// A field the registration details step renders and the sign-in page does not, used as the arrival
    /// barrier for the one navigation in this harness that follows a link rather than a URL (see
    /// <see cref="EnhancedNavigation"/>). Deliberately a field rather than the heading: copy changes and
    /// a barrier that fails on a reworded sentence is a barrier that gets deleted.
    /// </summary>
    private const string RegistrationDetailsMarker = "#display-name";

    private static readonly TimeSpan CeremonyGrace = TimeSpan.FromSeconds(4);

    /// <summary>How long a surface has to arrive — the same thirty seconds every page operation gets.</summary>
    private static readonly TimeSpan SurfacePatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The default administrator. The password is a passphrase rather than a short scramble because
    /// §3.2 requires twelve characters and refuses nothing else — and because a fixture that fails the
    /// policy would fail inside a validation summary, which is a tedious thing to diagnose.
    /// </summary>
    internal static readonly AdministratorAccount DefaultAdministrator =
        new("e2e.administrator", "End To End", "correct horse battery staple");

    /// <summary>
    /// Walks the whole four-step wizard: account details, a real WebAuthn attestation through the
    /// virtual authenticator, a real TOTP confirmation computed from the secret the page displays, and
    /// the commit. Returns the recovery codes shown once on the completion panel.
    ///
    /// <para>Every step posts back to <c>/setup</c> and redirects to <c>/setup</c> (the wizard is a
    /// post/redirect/get over a protected cookie), so the URL never changes and cannot be waited on.
    /// The waits below are therefore on the element each step is identified by, which is both more
    /// robust and more legible than a URL or a heading.</para>
    ///
    /// <para>Every navigation here is either a <c>GotoAsync</c> or a form post, so none of them is an
    /// enhanced navigation and none of them needs <see cref="EnhancedNavigation"/> — which is exactly
    /// why this journey has always worked while the registration one below had never once passed.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<string>> CompleteSetupAsync(IPage page, AdministratorAccount account)
    {
        await page.GotoAsync(AccountRoutes.Setup);

        // Step 1 — account details.
        await page.FillAsync("#username", account.Username);
        await page.FillAsync("#display-name", account.DisplayName);
        await page.FillAsync("#password", account.Password);
        await page.FillAsync("#confirm-password", account.Password);
        await page.ClickAsync("button:has-text('Continue to passkey')");

        // Step 2 — passkey registration. The <passkey-submit> element intercepts this button, runs
        // navigator.credentials.create() against the virtual authenticator, writes the credential JSON
        // into the form and submits it natively.
        ILocator passkeyButton = page.Locator("button[name='__passkeySubmit']");
        await passkeyButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await passkeyButton.ClickAsync();

        // Step 3 — the authenticator. The page renders the Base32 secret grouped in fours for manual
        // entry; Base32Text.TryDecode is forgiving of exactly that grouping, so it is passed verbatim.
        ILocator secret = page.Locator("p.totp-secret");
        await secret.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        string displayedSecret = await secret.InnerTextAsync();

        if (!Base32Text.TryDecode(displayedSecret, out byte[] totpSecret))
        {
            throw new InvalidOperationException(
                $"The setup page displayed a TOTP secret that is not Base32: '{displayedSecret}'.");
        }

        // §3.4's provider allows ±1 thirty-second step, so computing the code now and posting it a
        // moment later is safe even across a step boundary.
        await page.FillAsync("#code", Rfc6238Totp.ComputeCode(totpSecret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Confirm and review')");

        // Step 4 — review and commit.
        ILocator commitButton = page.Locator("button:has-text('Create administrator')");
        await commitButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await commitButton.ClickAsync();

        // The commit hashes nothing (the password was hashed at step 1) but does write the person, the
        // passkey, the TOTP secret, ten recovery codes and the role grant in one advisory-locked
        // transaction, then signs the administrator in on the same response.
        ILocator recoveryCodes = page.Locator("ul.recovery-codes li");
        await recoveryCodes.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        return await recoveryCodes.AllTextContentsAsync();
    }

    /// <summary>
    /// Registers a guest at <c>/register</c> with a passkey and no password (§4.3's passkey-first
    /// default), and returns once the browser has left the registration surface — wherever it led.
    ///
    /// <para><b>Reached the way a guest reaches it.</b> The caller starts on the sign-in page the join
    /// flow redirected them to (§4.4), and this follows the "Create an account" link rather than
    /// navigating to <c>/register</c> directly. That link carrying the return URL is the whole
    /// mechanism by which registering lands the guest back at the table they scanned; a scenario that
    /// typed the URL itself would be asserting on a path no guest can take.</para>
    ///
    /// <para><b>And following it is why this needs a barrier.</b> A link click is an <em>enhanced</em>
    /// navigation: the URL is pushed onto the history before the page is fetched, so waiting on the URL
    /// returns while the sign-in document is still on screen — and both surfaces have a
    /// <c>#username</c>. Typing then is worse than useless, because the DOM patch that follows resets
    /// every field to what the server rendered. That is the whole of why §16.3 scenarios 3, 4 and 6
    /// timed out on a button that was never going to appear: the details step was posting an empty
    /// username and refusing itself. See <see cref="EnhancedNavigation"/> for the mechanism, and
    /// <see cref="AssertFieldHoldsAsync"/> for the guard that makes any recurrence say so in one
    /// sentence instead of thirty seconds.</para>
    ///
    /// <para>The password field is left blank on purpose. It makes the passkey the only credential,
    /// which is the harder shape to get right — the <c>person</c> row must accept a NULL
    /// <c>password_hash</c> (§3.2) and the account must still be signable-into afterwards — and it
    /// removes an Argon2id hash from the critical path of a scenario that already waits on a rotation
    /// window.</para>
    ///
    /// <para>Requires a virtual authenticator on <paramref name="page"/>'s own context; see
    /// <see cref="RestaurantInstance.OpenIsolatedPageAsync"/>.</para>
    /// </summary>
    internal static async Task RegisterGuestWithPasskeyAsync(IPage page, GuestAccount account)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(account);

        ILocator createAccount = page.Locator($"a[href^='{AccountRoutes.Register}']").First;

        try
        {
            await createAccount.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"The sign-in page at '{page.Url}' offered no link to {AccountRoutes.Register}, so a"
                + " first-time guest arriving from a table's QR has nowhere to go (§4.4, §11.1).",
                exception);
        }

        // Step 1 — details. Blank password: a passkey-only account (§4.3). The wait is for the details
        // form itself rather than for the URL, for the reason spelled out above and in EnhancedNavigation.
        await EnhancedNavigation.FollowAsync(
            page,
            createAccount,
            RegistrationDetailsMarker,
            "the registration details step",
            SurfacePatience);

        // Checked after arrival rather than waited on. By here the document really is the destination,
        // so the URL is a fact rather than an intention — and a link that carried the guest somewhere
        // else entirely (a lost return URL, a redirect) is worth naming rather than discovering later
        // through a missing field.
        if (!IsRegistrationUrl(page.Url))
        {
            throw new InvalidOperationException(
                $"Following the sign-in page's 'Create an account' link landed on '{page.Url}' rather"
                + $" than on {AccountRoutes.Register}.");
        }

        await page.FillAsync("#username", account.Username);
        await page.FillAsync("#display-name", account.DisplayName);

        // Read back what is actually in the fields at the instant before the form goes. This is one
        // round trip and it converts an entire family of "the DOM moved under us" bugs — the one above,
        // and any future surface that patches itself between the keystrokes and the click — from a
        // thirty-second timeout on an unrelated element into a sentence naming the field and both values.
        await AssertFieldHoldsAsync(page, "#username", account.Username);
        await AssertFieldHoldsAsync(page, RegistrationDetailsMarker, account.DisplayName);

        await page.ClickAsync("form button[type='submit']:has-text('Continue')");

        // Step 2 — the credential. The <passkey-submit> element intercepts this button, runs
        // navigator.credentials.create() against the virtual authenticator, writes the credential JSON
        // into the form and submits it natively; the server verifies the attestation and commits the
        // whole account in one transaction.
        ILocator addPasskey = page.Locator("button[name='__passkeySubmit']");

        try
        {
            await addPasskey.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            // §4.3's details step refuses itself in a ValidationMessage rather than in a status panel,
            // and a refusal there is indistinguishable from a hung page unless somebody reads it out.
            string refusal = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"Registering '{account.Username}' never advanced from the details step to the"
                + $" credential step, so there was no passkey button to press. {refusal}",
                exception);
        }

        await addPasskey.ClickAsync();

        try
        {
            await page.WaitForURLAsync(
                url => !IsRegistrationUrl(url), new PageWaitForURLOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"Registering '{account.Username}' never left {AccountRoutes.Register}. {refusal}",
                exception);
        }
    }

    /// <summary>
    /// Signs out through an antiforgery-protected POST form, then waits for the header to offer a
    /// sign-in link again. That link exists only in the layout's <c>NotAuthorized</c> branch, so it is
    /// positive proof the cookie is gone rather than a guess about the redirect — and waiting for it
    /// also settles the navigation the click started.
    ///
    /// <para><b>Works from inside §3.5's pipeline, and that took <c>.First</c>.</b> A principal with an
    /// outstanding obligation cannot reach <c>/</c> — <see cref="ObligationsEnforcement.IsExemptPath"/>
    /// exempts sign-out and the two obligation pages and nothing else, so the <c>GotoAsync</c> below is
    /// redirected to whichever obligation page is next. Both of those render a sign-out form of their
    /// own beside the header's ("Not ready right now?", "Done for now?"), because §3.5 promises that
    /// leaving is always possible; a bare <c>Locator</c> then matches two elements and every Locator
    /// method that acts on one throws a strict-mode violation. Taking the first is safe rather than
    /// merely convenient: the two forms are identical in effect — same endpoint, same token, neither
    /// carries a <c>returnUrl</c> — so <c>SafeLocalReturnUrl(null)</c> sends both to <c>/</c>. The
    /// header's comes first in document order, and it is the one a person would use.</para>
    ///
    /// <para>Signing a <em>trapped</em> principal out is not a corner case. §16.3 scenario 12 has to do
    /// it twice, and it has to be a real sign-out rather than an abandoned tab: the middleware decides
    /// from the cookie's claims, not from the row, so a session left holding stale obligation claims
    /// would keep being redirected after the obligations were cleared elsewhere — and an assertion that
    /// the pipeline had <em>released</em> that account could not then tell a fresh cookie from a page
    /// noticing its own claim was stale.</para>
    /// </summary>
    internal static async Task SignOutAsync(IPage page)
    {
        await page.GotoAsync("/");

        ILocator signOutButton = page.Locator("form.sign-out-form button[type='submit']").First;
        await signOutButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await signOutButton.ClickAsync();

        await page
            .Locator($"nav.app-session a[href='{AccountRoutes.SignIn}']")
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// Signs in with a passkey and returns once the sign-in page has been left — wherever it led.
    /// The caller asserts on the destination, which is the point of §16.3 scenario 13.
    ///
    /// <para><c>passkey.js</c> starts a conditional-mediation request the moment the page loads, and an
    /// authenticator that simulates presence can satisfy it with no gesture at all — in which case the
    /// form has already been submitted and there is no button left to press. So the button is only
    /// driven when that has demonstrably not happened. Clicking is still safe if a conditional request
    /// is merely in flight: the element aborts its own pending request before starting a new one.</para>
    /// </summary>
    internal static async Task SignInWithPasskeyAsync(IPage page, string username)
    {
        await page.GotoAsync(AccountRoutes.SignIn);

        if (await IsStillOnSignInPageAsync(page, CeremonyGrace))
        {
            await page.FillAsync("#username", username);
            await page.ClickAsync("button[name='__passkeySubmit']");
        }

        await page.WaitForURLAsync(
            HasLeftSignInPage,
            new PageWaitForURLOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// Signs in with a username and a password (§3.3) and returns once the sign-in page has been left —
    /// wherever it led, which the caller asserts on.
    ///
    /// <para><b>Where it leads is often not the destination.</b> A password sign-in that succeeds
    /// navigates to the return URL, and <c>ObligationsMiddleware</c> intercepts that navigation whenever
    /// a §3.5 flag is set — so a staff account signing in on its temporary password lands on the
    /// forced-change page rather than anywhere it asked for. That is the behaviour §16.3 scenario 12 is
    /// about and scenario 9 walks through on its way to the till, and it is why this returns on "left the
    /// sign-in page" rather than on any particular arrival.</para>
    ///
    /// <para>The submit button is named by exclusion rather than by its class or its words: this form
    /// carries two, and the other is the <c>&lt;passkey-submit&gt;</c> one, which is the only one with a
    /// <c>name</c> attribute. "Sign in" as text matches both, because the second says "Sign in with a
    /// passkey".</para>
    ///
    /// <para>No virtual authenticator is needed on the page's context. <c>passkey.js</c> does start a
    /// conditional-mediation request on load, but a context with no authenticator simply never satisfies
    /// it and the password path is untouched — which is also §17's accepted position that "counter role
    /// may operate password-only".</para>
    /// </summary>
    internal static async Task SignInWithPasswordAsync(IPage page, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(AccountRoutes.SignIn);

        await page.FillAsync("#username", username);
        await page.FillAsync("#password", password);

        await page.ClickAsync("form button[type='submit']:not([name='__passkeySubmit'])");

        try
        {
            await page.WaitForURLAsync(
                HasLeftSignInPage,
                new PageWaitForURLOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"Signing '{username}' in with a password never left {AccountRoutes.SignIn}. {refusal}",
                exception);
        }
    }

    /// <summary>
    /// Clears §3.5's obligation (1) on the page it traps a principal on: replaces the temporary password
    /// with a real one and returns once the pipeline has released the principal.
    ///
    /// <para><b>Being on the right page is asserted rather than assumed, and that is the point.</b> A
    /// caller reaching here having landed somewhere else means the obligation did not fire — an account
    /// created with <c>must_change_password</c> walked straight past it on a temporary password, which
    /// is a §3.5 hole rather than a harness inconvenience. It deserves a sentence naming where the
    /// browser actually is.</para>
    ///
    /// <para>Completion is "the browser left the forced-change page". <c>ChangePasswordRequired.razor</c>
    /// clears the flag in the same store update that persists the new hash, records a
    /// <c>forced_password_change_completed</c> event, re-issues the cookie so the obligation claim
    /// disappears with it, and only then navigates — so leaving is the whole of that sequence having
    /// happened, and a page that refused the new password stays exactly where it was with its reasons
    /// on screen.</para>
    /// </summary>
    internal static async Task CompleteForcedPasswordChangeAsync(
        IPage page,
        string temporaryPassword,
        string newPassword)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!IsForcedPasswordChangeUrl(page.Url))
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"The browser is not on {AccountRoutes.ForcedPasswordChange}, so there is no forced"
                + " password change to complete. An account created by §3.7 carries"
                + " must_change_password, and §3.5 admits such a principal to no authenticated endpoint"
                + $" except sign-out and the pipeline's own pages until it clears. {surface}");
        }

        await page.FillAsync("#current-password", temporaryPassword);
        await page.FillAsync("#new-password", newPassword);
        await page.FillAsync("#confirm-password", newPassword);

        await page.ClickAsync("button:has-text('Set new password')");

        try
        {
            await page.WaitForURLAsync(
                url => !IsForcedPasswordChangeUrl(url),
                new PageWaitForURLOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"The forced password change was never accepted, so the principal is still held by"
                + $" §3.5's pipeline. {refusal}",
                exception);
        }
    }

    /// <summary>
    /// Clears §3.5's obligation (2) on the page it traps a principal on: scans the authenticator the
    /// page is offering, confirms a code computed from it, and follows the panel's Continue link to
    /// wherever the pipeline said the principal was originally headed. Returns the fresh recovery-code
    /// set, read off the panel before the link is followed — the one moment those exist in the clear.
    ///
    /// <para><b>The destination is the caller's to name, not this method's.</b> §3.5 step (3) is
    /// "continue to the originally requested URL", and <c>EnrollTotpRequired.razor</c> honours that by
    /// pointing Continue at the <c>ReturnUrl</c> the middleware carried across. Baking <c>/</c> in here
    /// would work for the one scenario that asks for nothing and would quietly stop being a barrier for
    /// any scenario that asks for something — which is the single way to get an
    /// <see cref="EnhancedNavigation"/> arrival selector wrong.</para>
    ///
    /// <para>Being on the right page is asserted rather than assumed, for the reason
    /// <see cref="CompleteForcedPasswordChangeAsync"/> gives: a caller that landed elsewhere means the
    /// obligation did not fire, and an account whose authenticator an administrator wiped yet which
    /// reached a destination anyway is a §3.5 hole rather than a harness inconvenience.</para>
    /// </summary>
    /// <param name="page">A page held on the forced re-enrollment surface.</param>
    /// <param name="destinationSelector">
    /// Something only the destination renders — see <see cref="EnhancedNavigation.FollowAsync"/>. For a
    /// principal who asked for nothing in particular that is <see cref="LandingPageMarker"/>.
    /// </param>
    /// <param name="destinationDescription">What that selector means, in words, for the failure message.</param>
    internal static async Task<IReadOnlyList<string>> CompleteForcedTotpEnrollmentAsync(
        IPage page,
        string destinationSelector,
        string destinationDescription)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(destinationSelector);

        if (!IsForcedTotpEnrollmentUrl(page.Url))
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"The browser is not on {AccountRoutes.ForcedTotpEnrollment}, so there is no forced"
                + " authenticator re-enrollment to complete. §3.7's reset clears an enrolled secret and"
                + " sets must_enroll_totp, and §3.5 admits such a principal to no authenticated endpoint"
                + $" except sign-out and the pipeline's own pages until it clears. {surface}");
        }

        IReadOnlyList<string> recoveryCodes = await ConfirmDisplayedAuthenticatorAsync(
            page, AccountRoutes.ForcedTotpEnrollment);

        // §3.5's release, as the surface offers it. Confirmation cleared must_enroll_totp, rotated the
        // stamp and re-issued the cookie in that order, so the obligation claim is gone from this
        // session and the middleware lets the next request through — which is what makes following this
        // link a claim about the pipeline rather than about a hyperlink.
        ILocator continueLink = page.Locator("div.form-actions a:has-text('Continue')").First;

        try
        {
            await continueLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                "The forced re-enrollment panel showed its recovery codes but offered no way onward, so"
                + $" a principal §3.5 has just released has nowhere to go. {surface}",
                exception);
        }

        await EnhancedNavigation.FollowAsync(
            page, continueLink, destinationSelector, destinationDescription, SurfacePatience);

        return recoveryCodes;
    }

    /// <summary>
    /// Enrolls an authenticator through the <em>voluntary</em> §3.4 surface and returns the recovery-code
    /// set it shows once. Leaves the browser on that panel: its "Done" link goes to <c>/</c> and nothing
    /// here needs to be there, so the caller's next <c>GotoAsync</c> is both cheaper and clearer than a
    /// link click with an arrival barrier attached to it.
    ///
    /// <para><b>Why a scenario would want the voluntary page at all.</b> §3.7's create-staff form writes
    /// <c>must_change_password</c> and nothing else — no secret, and deliberately not
    /// <c>must_enroll_totp</c> — so an administrator cannot arrange a TOTP-enrolled account and neither
    /// can a fixture. The account has to enrol itself, exactly as a real staff member does, which is why
    /// §16.3 scenario 12 spends four form posts getting to the state its first sentence starts from.</para>
    ///
    /// <para>Requires the principal to be past §3.5's pipeline: this page is a normal authenticated
    /// destination and is <em>not</em> in the exempt list, so a principal with an outstanding obligation
    /// is redirected to the obligation page instead of reaching it. That redirect is what the failure
    /// message below reads out.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<string>> EnrollAuthenticatorAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(AccountRoutes.TotpEnrollment);

        return await ConfirmDisplayedAuthenticatorAsync(page, AccountRoutes.TotpEnrollment);
    }

    /// <summary>
    /// Registers a passkey on the <em>voluntary</em> §3.3 surface and returns how many the account holds
    /// afterwards, counted off the list the page re-renders.
    ///
    /// <para>Requires a virtual authenticator on <paramref name="page"/>'s own context, and a credential
    /// minted here belongs to <em>that</em> context for good — a WebAuthn private key never leaves the
    /// authenticator that made it. So the browser that registers a passkey is the only browser that can
    /// later sign in with it, which is the whole reason §16.3 scenario 12 gives its staff member two:
    /// a device that holds the passkey, and a terminal that does not and therefore signs in by password
    /// without <c>passkey.js</c>'s conditional-mediation request quietly answering first.</para>
    ///
    /// <para>Completion is the page's own confirmation rather than the row appearing. The add is a
    /// post/redirect/get carrying a one-line status, and the row is written in the redirected-from
    /// request; waiting only on the list would also be satisfied by a passkey that was already there,
    /// which for a caller that intends to assert on the count is the wrong barrier.</para>
    /// </summary>
    internal static async Task<int> AddPasskeyAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(AccountRoutes.Passkeys);

        ILocator addPasskey = page.Locator("button[name='__passkeySubmit']").First;

        try
        {
            await addPasskey.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"{AccountRoutes.Passkeys} offered no way to add a passkey. It is a normal authenticated"
                + " destination rather than a §3.5 pipeline page, so the usual cause is an outstanding"
                + $" obligation having redirected the browser somewhere else entirely. {surface}",
                exception);
        }

        await addPasskey.ClickAsync();

        ILocator confirmation = page.Locator("p.status-success").First;

        try
        {
            await confirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                "Registering a passkey was not confirmed, so either the ceremony produced nothing or the"
                + " attestation was refused. A virtual authenticator on this page's own context is"
                + $" required; see RestaurantInstance.OpenIsolatedPageAsync. {surface}",
                exception);
        }

        string message = await ScreenText.DeclaredAsync(confirmation);

        if (!message.Contains("Passkey added", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Registering a passkey reported '{message}', which is some other outcome — the page"
                + " flashes a rename and a removal through the same element.");
        }

        return await page.Locator("li.passkey-row").CountAsync();
    }

    /// <summary>
    /// The half the two authenticator surfaces share: read the secret the page is displaying, compute a
    /// code from it, post it, and come back with the recovery codes the confirmation shows.
    ///
    /// <para>The secret is taken off the screen rather than generated here, and that is the point of the
    /// exercise. §3.4 persists nothing until a code verifies — the unconfirmed secret lives only in a
    /// Data-Protection-protected ticket in the page's own markup — so a harness that invented its own
    /// secret would be confirming an enrollment the server had never proposed. Reading it back is what
    /// makes this the ceremony a person performs.</para>
    ///
    /// <para>The page renders the Base32 secret grouped in fours for manual entry and
    /// <c>Base32Text.TryDecode</c> is forgiving of exactly that grouping, so it is passed verbatim.
    /// §3.4's provider allows ±1 thirty-second step, so computing the code now and posting it a moment
    /// later survives a step boundary.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ConfirmDisplayedAuthenticatorAsync(
        IPage page,
        string surfacePath)
    {
        ILocator secret = page.Locator("p.totp-secret").First;

        try
        {
            await secret.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"{surfacePath} displayed no authenticator key to scan, so there is nothing to confirm."
                + $" {surface}",
                exception);
        }

        string displayedSecret = await secret.InnerTextAsync();

        if (!Base32Text.TryDecode(displayedSecret, out byte[] totpSecret))
        {
            throw new InvalidOperationException(
                $"{surfacePath} displayed an authenticator key that is not Base32: '{displayedSecret}'.");
        }

        await page.FillAsync("#totp-code", Rfc6238Totp.ComputeCode(totpSecret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Confirm enrollment')");

        ILocator recoveryCodes = page.Locator("ul.recovery-codes li");

        try
        {
            await recoveryCodes.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            // Both surfaces refuse a bad code in place, with the QR still on screen, so the page a
            // caller is looking at when this throws is the same one it was looking at before — and the
            // refusal it is showing is the only thing that explains why.
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                $"{surfacePath} did not accept the code computed from the key it had just displayed, so"
                + " no secret was written and no recovery codes were issued. Either the ticket carrying"
                + $" the unconfirmed secret was rejected, or the code itself did not verify. {surface}",
                exception);
        }

        IReadOnlyList<string> issued = await recoveryCodes.AllTextContentsAsync();

        return [.. issued.Select(ScreenText.Collapse)];
    }

    private static async Task<bool> IsStillOnSignInPageAsync(IPage page, TimeSpan grace)
    {
        try
        {
            await page.WaitForURLAsync(
                HasLeftSignInPage,
                new PageWaitForURLOptions { Timeout = (float)grace.TotalMilliseconds });

            return false;
        }
        catch (PlaywrightException)
        {
            return true;
        }
    }

    /// <summary>
    /// Refuses to submit a form whose fields do not hold what was typed into them.
    ///
    /// <para>Not defensive programming for its own sake. The failure this catches is the one that
    /// cannot be diagnosed from where it surfaces: a value silently reset by a DOM patch produces a
    /// perfectly ordinary validation refusal on the next screen, and the scenario then times out
    /// waiting for a screen after that. Two round trips here buy a message that names the field, what
    /// it holds, and what it should have held.</para>
    /// </summary>
    private static async Task AssertFieldHoldsAsync(IPage page, string selector, string expected)
    {
        string actual = await page.InputValueAsync(selector);

        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{selector}' holds '{actual}' rather than '{expected}' at the moment the form was about"
            + " to be submitted, so the post would carry the wrong value and the surface would refuse"
            + " itself for a reason unrelated to what is under test. The usual cause is a DOM patch"
            + " landing between the keystrokes and the click: Blazor's enhanced navigation assigns"
            + " every input the value the server rendered, so anything typed while its fetch was still"
            + " in flight is erased. Whatever was supposed to make the destination surface a completed"
            + " fact before typing began did not (see EnhancedNavigation).");
    }

    /// <summary>
    /// True once the browser is somewhere other than the sign-in page itself. Compares the path
    /// exactly rather than by prefix on purpose: <c>/sign-in/two-factor</c> must count as "left", so
    /// that a scenario asserting no TOTP challenge fails with the URL it actually landed on instead of
    /// with an unexplained timeout.
    /// </summary>
    private static bool HasLeftSignInPage(string url)
        => !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.AbsolutePath, AccountRoutes.SignIn, StringComparison.Ordinal);

    /// <summary>
    /// True while the browser is on the registration page itself. Compared by exact path, so the
    /// ceremony endpoint underneath it (<c>/register/passkey/creation-options</c>) would not count —
    /// though the element fetches that rather than navigating to it, so in practice this only ever
    /// sees the page.
    /// </summary>
    private static bool IsRegistrationUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && string.Equals(parsed.AbsolutePath, AccountRoutes.Register, StringComparison.Ordinal);

    /// <summary>
    /// True while the browser is on §3.5's forced password-change page. The path is compared exactly and
    /// the query string is ignored on purpose: the middleware appends <c>?ReturnUrl=…</c>, and the
    /// destination being carried across is a fact about the redirect rather than about which page this is.
    /// </summary>
    private static bool IsForcedPasswordChangeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && string.Equals(
                parsed.AbsolutePath, AccountRoutes.ForcedPasswordChange, StringComparison.Ordinal);

    /// <summary>
    /// True while the browser is on §3.5's forced re-enrollment page. Path compared exactly, query
    /// ignored, for the same reason as above — and deliberately not by prefix, so that the voluntary
    /// <c>/account/enroll-totp</c> can never be mistaken for its obligation counterpart. The two render
    /// nearly the same form and differ in the one thing that matters: only the forced page clears a flag,
    /// and only it records <c>forced_totp_enrollment_completed</c>.
    /// </summary>
    private static bool IsForcedTotpEnrollmentUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && string.Equals(
                parsed.AbsolutePath, AccountRoutes.ForcedTotpEnrollment, StringComparison.Ordinal);

    /// <summary>
    /// Whatever the account surface has to say about why it did not proceed: which step it is showing,
    /// every refusal and validation message on it, and the URL it is sitting on.
    ///
    /// <para>All of them rather than the first. A details step that refuses can refuse in more than one
    /// field at once, and "Choose a username." on its own would have explained these three scenarios
    /// the day they were written.</para>
    /// </summary>
    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        string heading = await HeadingOrNothingAsync(page);

        // `.status-error` rather than `p.status-error`, and the widening is load-bearing rather than
        // tidying: ChangePasswordRequired.razor renders its refusals as a `ul.status-error` of `li`
        // elements, because Identity hands back a list. The narrower selector matched none of them, so
        // the one page in this application whose whole job is to refuse would have described itself as
        // reporting no error. Every existing caller is unaffected — the old set is a subset — and any
        // element carrying the class now reads out whole, list items included.
        ILocator problems = page.Locator(".status-error, .validation-message");

        if (await problems.CountAsync() == 0)
        {
            return $"The page is headed '{heading}' and reports no error; the browser is at '{page.Url}'.";
        }

        IReadOnlyList<string> all = await problems.AllInnerTextsAsync();
        string message = string.Join(" | ", all.Select(text => text.Trim()));

        return $"The page is headed '{heading}' and reports: {message} (browser at '{page.Url}').";
    }

    /// <summary>The page's first <c>h1</c>, or a placeholder — a blank error page has none at all.</summary>
    private static async Task<string> HeadingOrNothingAsync(IPage page)
    {
        ILocator headings = page.Locator("h1");

        if (await headings.CountAsync() == 0)
        {
            return "(no heading)";
        }

        return (await headings.First.InnerTextAsync()).Trim();
    }
}
