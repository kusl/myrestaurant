using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Identity;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// The guest-side journeys at <c>/table/{id}</c> — scanning a code, and turning the resulting join
/// grant into membership of the table's sitting (TECHNICAL_SPECIFICATION §4.4, §5.1, §11.1).
///
/// <para><b>Why "scan" is a navigation.</b> §16.3 scenario 3 says "guest scans (simulated URL from
/// current token)", and that is exactly what a scan is: the camera resolves a QR to
/// <c>{origin}/table/{id}?token=…</c> and the browser goes there. Nothing about the flow under test
/// lives in the optics, so the harness navigates to the same path.</para>
///
/// <para><b>The path is relative, and that is not laziness.</b> The absolute URL a display encodes is
/// built from <c>RESTAURANT_PUBLIC_ORIGIN</c>, which the harness deliberately sets to
/// <c>https://localhost:{port}</c> while Kestrel serves plain HTTP on that port — the mismatch is what
/// lets §13's https requirement and Chromium's localhost-as-secure-context rule hold at the same time
/// (see <see cref="RestaurantInstance"/>). Navigating to that absolute URL would reach nothing. The
/// token is the whole of what a scan carries that a bare path does not, and it is computed from the
/// real join secret; scenario 2 separately asserts that a real screen encodes exactly the code this
/// table's secret produces, so between the two nothing is assumed.</para>
///
/// <para><b>The four outcomes.</b> §4.4 resolves a GET on this page to exactly one of member, confirm,
/// a redirect to sign-in, or the friendly expiry page, and deliberately makes the last of those
/// identical for every kind of failure. <see cref="JoinStageOnScreen"/> names which one is showing so
/// a scenario's failure message says "it offered the join button" rather than quoting a heading.</para>
/// </summary>
internal static class TableJourneys
{
    /// <summary>The §4.4 expiry page's heading — one wording for every failure, by design.</summary>
    internal const string ExpiredHeading = "That code has expired";

    /// <summary>What a GET on <c>/table/{id}</c> resolved to, in §4.4's own terms.</summary>
    internal enum JoinStage
    {
        /// <summary>The friendly re-scan page: no usable token, no live grant, or an unknown table.</summary>
        Expired,

        /// <summary>Signed in, holding a live grant, not yet a member: the join button is on screen.</summary>
        Confirm,

        /// <summary>A member of this table's open sitting: the order surface itself.</summary>
        Member,

        /// <summary>Anonymous with a live grant: the browser was sent to sign in (or register).</summary>
        SentToSignIn,
    }

    /// <summary>
    /// Follows a table's current join URL as a scan would (§4.3, §4.4) and reports where it landed. The
    /// token is computed by the caller from the secret it read out of the row, so this is a code the
    /// server will really verify — not one the harness invented.
    /// </summary>
    internal static async Task<JoinStage> ScanAsync(IPage page, Guid tableIdentifier, string token)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(JoinPath(tableIdentifier, token));

        return await JoinStageOnScreen(page);
    }

    /// <summary>The scan path, relative to the instance's base URL — see the type remarks.</summary>
    internal static string JoinPath(Guid tableIdentifier, string token)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"/table/{tableIdentifier:D}?token={Uri.EscapeDataString(token)}");

    /// <summary>
    /// Presses the join button (§4.4) and waits for the member surface. The join is a
    /// post/redirect/get — the POST consumes the grant, opens or joins the sitting in one transaction
    /// (§5.1), and redirects back to <c>/table/{id}?joined=yes</c> — so the observable outcome is the
    /// confirmation on the page that follows, not the click.
    /// </summary>
    internal static async Task JoinAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator joinButton = page.Locator("form button[type='submit']:has-text('Join')").First;

        try
        {
            await joinButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            JoinStage stage = await JoinStageOnScreen(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There was no join button to press: the table page resolved to {stage} (§4.4)."),
                exception);
        }

        await joinButton.ClickAsync();

        ILocator joined = page.Locator("p.status-success");

        try
        {
            await joined.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            JoinStage stage = await JoinStageOnScreen(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    // Every operand is an interpolated string, and that is load-bearing rather than
                    // stylistic. string.Create's second parameter is a `ref DefaultInterpolatedStringHandler`,
                    // and C# only converts an addition to a handler when the whole additive expression is
                    // composed of interpolated strings; one bare "…" literal in the chain makes the result a
                    // plain string and the call fails to bind with CS1620. A hole-less $"…" still counts.
                    $"Joining did not confirm; the table page is now showing {stage}. A grant is"
                    + $" single-use and is cleared whatever the outcome (§4.4), so if this was a"
                    + $" refusal the grant is already spent and a retry will not help."),
                exception);
        }
    }

    /// <summary>
    /// Which of §4.4's outcomes the page is currently showing. Read from the markup rather than from
    /// the URL, because three of the four share one: only the sign-in redirect changes the path.
    /// </summary>
    internal static async Task<JoinStage> JoinStageOnScreen(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (Uri.TryCreate(page.Url, UriKind.Absolute, out Uri? parsed)
            && parsed.AbsolutePath.StartsWith(AccountRoutes.SignIn, StringComparison.Ordinal))
        {
            return JoinStage.SentToSignIn;
        }

        string heading = (await page.Locator("h1").First.InnerTextAsync()).Trim();

        if (string.Equals(heading, ExpiredHeading, StringComparison.Ordinal))
        {
            return JoinStage.Expired;
        }

        // Confirm and Member both render the table's label as the heading, so the eyebrow above it is
        // what separates them — "Join a table" versus "Your table". It is one word of copy either way,
        // which is why the join button is checked as well rather than trusted alone.
        return await page.Locator("form button[type='submit']:has-text('Join')").CountAsync() > 0
            ? JoinStage.Confirm
            : JoinStage.Member;
    }
}
