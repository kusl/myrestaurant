using Microsoft.Playwright;
using MyRestaurant.Domain.Security;
using MyRestaurant.WebApplication.Identity;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record AdministratorAccount(string Username, string DisplayName, string Password);

internal sealed record GuestAccount(string Username, string DisplayName);

internal static class AccountJourneys
{
    internal const int ExpectedRecoveryCodeCount = 10;

    internal const string LandingPageMarker = "h1.landing-title";

    private const string RegistrationDetailsMarker = "#display-name";

    private static readonly TimeSpan CeremonyGrace = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan SurfacePatience = TimeSpan.FromSeconds(30);

    internal static readonly AdministratorAccount DefaultAdministrator =
        new("e2e.administrator", "End To End", "correct horse battery staple");

    internal static async Task<IReadOnlyList<string>> CompleteSetupAsync(IPage page, AdministratorAccount account)
    {
        await page.GotoAsync(AccountRoutes.Setup);

        await page.FillAsync("#username", account.Username);
        await page.FillAsync("#display-name", account.DisplayName);
        await page.FillAsync("#password", account.Password);
        await page.FillAsync("#confirm-password", account.Password);
        await page.ClickAsync("button:has-text('Continue to passkey')");

        ILocator passkeyButton = page.Locator("button[name='__passkeySubmit']");
        await passkeyButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await passkeyButton.ClickAsync();

        ILocator secret = page.Locator("p.totp-secret");
        await secret.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        string displayedSecret = await secret.InnerTextAsync();

        if (!Base32Text.TryDecode(displayedSecret, out byte[] totpSecret))
        {
            throw new InvalidOperationException(
                $"The setup page displayed a TOTP secret that is not Base32: '{displayedSecret}'.");
        }

        await page.FillAsync("#code", Rfc6238Totp.ComputeCode(totpSecret, DateTimeOffset.UtcNow));
        await page.ClickAsync("button:has-text('Confirm and review')");

        ILocator commitButton = page.Locator("button:has-text('Create administrator')");
        await commitButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await commitButton.ClickAsync();

        ILocator recoveryCodes = page.Locator("ul.recovery-codes li");
        await recoveryCodes.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        return await recoveryCodes.AllTextContentsAsync();
    }

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

        await EnhancedNavigation.FollowAsync(
            page,
            createAccount,
            RegistrationDetailsMarker,
            "the registration details step",
            SurfacePatience);

        if (!IsRegistrationUrl(page.Url))
        {
            throw new InvalidOperationException(
                $"Following the sign-in page's 'Create an account' link landed on '{page.Url}' rather"
                + $" than on {AccountRoutes.Register}.");
        }

        await page.FillAsync("#username", account.Username);
        await page.FillAsync("#display-name", account.DisplayName);

        await AssertFieldHoldsAsync(page, "#username", account.Username);
        await AssertFieldHoldsAsync(page, RegistrationDetailsMarker, account.DisplayName);

        await page.ClickAsync("form button[type='submit']:has-text('Continue')");

        ILocator addPasskey = page.Locator("button[name='__passkeySubmit']");

        try
        {
            await addPasskey.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
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

    internal static async Task<IReadOnlyList<string>> EnrollAuthenticatorAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(AccountRoutes.TotpEnrollment);

        return await ConfirmDisplayedAuthenticatorAsync(page, AccountRoutes.TotpEnrollment);
    }

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

    private static bool HasLeftSignInPage(string url)
        => !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.AbsolutePath, AccountRoutes.SignIn, StringComparison.Ordinal);

    private static bool IsRegistrationUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && string.Equals(parsed.AbsolutePath, AccountRoutes.Register, StringComparison.Ordinal);

    private static bool IsForcedPasswordChangeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && string.Equals(
                parsed.AbsolutePath, AccountRoutes.ForcedPasswordChange, StringComparison.Ordinal);

    private static bool IsForcedTotpEnrollmentUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && string.Equals(
                parsed.AbsolutePath, AccountRoutes.ForcedTotpEnrollment, StringComparison.Ordinal);

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        string heading = await HeadingOrNothingAsync(page);

        ILocator problems = page.Locator(".status-error, .validation-message");

        if (await problems.CountAsync() == 0)
        {
            return $"The page is headed '{heading}' and reports no error; the browser is at '{page.Url}'.";
        }

        IReadOnlyList<string> all = await problems.AllInnerTextsAsync();
        string message = string.Join(" | ", all.Select(text => text.Trim()));

        return $"The page is headed '{heading}' and reports: {message} (browser at '{page.Url}').";
    }

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
