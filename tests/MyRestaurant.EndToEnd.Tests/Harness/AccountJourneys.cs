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
/// The account journeys more than one §16.3 scenario walks: the first-administrator wizard, sign-out,
/// and passkey sign-in. Kept out of the scenario file so a scenario reads as its own assertion rather
/// than as thirty lines of form filling — and so that when a surface changes, one place changes.
/// </summary>
internal static class AccountJourneys
{
    /// <summary>Ten single-use recovery codes are minted by the §3.6 bootstrap.</summary>
    internal const int ExpectedRecoveryCodeCount = 10;

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
    /// Signs out through the header's antiforgery-protected POST form, then waits for the header to
    /// offer a sign-in link again. That link exists only in the layout's <c>NotAuthorized</c> branch,
    /// so it is positive proof the cookie is gone rather than a guess about the redirect — and waiting
    /// for it also settles the navigation the click started.
    /// </summary>
    internal static async Task SignOutAsync(IPage page)
    {
        await page.GotoAsync("/");

        ILocator signOutButton = page.Locator("form.sign-out-form button[type='submit']");
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
