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

    private static readonly TimeSpan CeremonyGrace = TimeSpan.FromSeconds(4);

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

        await createAccount.ClickAsync();

        // Step 1 — details. Blank password: a passkey-only account (§4.3).
        await page.WaitForURLAsync(
            url => IsRegistrationUrl(url), new PageWaitForURLOptions { Timeout = 30_000 });

        await page.FillAsync("#username", account.Username);
        await page.FillAsync("#display-name", account.DisplayName);
        await page.ClickAsync("button:has-text('Continue')");

        // Step 2 — the credential. The <passkey-submit> element intercepts this button, runs
        // navigator.credentials.create() against the virtual authenticator, writes the credential JSON
        // into the form and submits it natively; the server verifies the attestation and commits the
        // whole account in one transaction.
        ILocator addPasskey = page.Locator("button[name='__passkeySubmit']");
        await addPasskey.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await addPasskey.ClickAsync();

        try
        {
            await page.WaitForURLAsync(
                url => !IsRegistrationUrl(url), new PageWaitForURLOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeRefusalAsync(page);

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
    /// Whatever the account surface has to say about why it did not proceed: a refusal panel, a
    /// validation message, or — when there is neither — the URL it is sitting on.
    /// </summary>
    private static async Task<string> DescribeRefusalAsync(IPage page)
    {
        ILocator problems = page.Locator("p.status-error, .validation-message");

        if (await problems.CountAsync() == 0)
        {
            return $"The page reports no error; the browser is at '{page.Url}'.";
        }

        string message = (await problems.First.InnerTextAsync()).Trim();

        return $"The page reports: {message} (browser at '{page.Url}').";
    }
}

