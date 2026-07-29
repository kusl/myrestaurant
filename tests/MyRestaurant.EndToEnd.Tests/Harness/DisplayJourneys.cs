using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Displays;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// The display-device journeys the §16.3 scenarios walk: redeeming a pairing code at
/// <c>/display/pair</c>, and watching the rotating QR on <c>/display/{table}</c>
/// (TECHNICAL_SPECIFICATION §4.2, §4.3, §11.5).
///
/// <para><b>These always run on their own page, in their own browser context.</b> Not for tidiness:
/// <c>DisplayDeviceAuthenticationMiddleware</c> ignores the device credential whenever the Identity
/// cookie has already authenticated the request — "a signed-in person always wins", so that a member of
/// staff who opens the display URL on a paired tablet is themselves rather than the screen. Pair inside
/// the administrator's browser and the resulting surface would resolve as
/// <c>DisplayStage.NotPaired</c> and bounce to <c>/display/pair</c>, for a reason that looks nothing like
/// the cause. <see cref="RestaurantInstance.OpenIsolatedPageAsync"/> is what a tablet is.</para>
/// </summary>
internal static class DisplayJourneys
{
    /// <summary>
    /// The QR's path element. Scoped to the surface's own id so it cannot accidentally match the
    /// authenticator QR on an account page, and so a selector failure names the surface it wanted.
    /// </summary>
    private const string JoinQrPathSelector = "#table-display-surface svg.join-qr-svg path";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Redeems a pairing code as an unpaired screen would (§4.2) and returns the table the device landed
    /// on, read out of the URL the application redirected to rather than assumed from the caller — which
    /// is what makes "the code paired this device to <em>that</em> table" an assertion the scenario can
    /// make rather than a premise it supplies.
    /// </summary>
    internal static async Task<Guid> PairAsync(IPage page, string pairingCode, string deviceLabel)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(DisplayRoutes.Pair);

        await page.FillAsync("#pairing-code", pairingCode);
        await page.FillAsync("#device-label", deviceLabel);
        await page.ClickAsync("button:has-text('Pair this display')");

        // Pairing's last act writes the year-long credential cookie and redirects (§4.2), so the URL is
        // the observable outcome. A refusal instead re-renders the form with one deliberately vague
        // sentence (§4.2 forbids an oracle), which is unhelpful to a prober and equally unhelpful to
        // whoever is reading this failure — hence quoting it verbatim into the exception.
        try
        {
            await page.WaitForURLAsync(IsTableDisplayUrl, new PageWaitForURLOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "Pairing the display did not reach a table display surface. "
                + await DescribeRefusalAsync(page),
                exception);
        }

        if (!TryReadTableIdentifier(page.Url, out Guid tableIdentifier))
        {
            throw new InvalidOperationException(
                $"Pairing landed on '{page.Url}', which carries no table identifier.");
        }

        return tableIdentifier;
    }

    /// <summary>
    /// The <c>d</c> attribute of the QR currently on screen. Waits for the element to be
    /// <em>attached</em> rather than visible on purpose: the offline curtain <c>js/display.js</c> raises
    /// over a stale code (§11.5) sits on top of this element, and a scenario diagnosing a frozen display
    /// must still be able to read what it froze on.
    /// </summary>
    internal static async Task<string> ReadJoinQrPathAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator path = page.Locator(JoinQrPathSelector).First;
        await path.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 60_000,
        });

        return await path.GetAttributeAsync("d")
            ?? throw new InvalidOperationException(
                "The join QR on the display has a <path> element with no 'd' attribute.");
    }

    /// <summary>
    /// Polls the QR until <paramref name="isAcceptable"/> is satisfied, and returns the path that
    /// satisfied it.
    ///
    /// <para>Polling rather than a Blazor-aware wait because the thing being waited on is a
    /// <em>server</em> timer: §4.3 has the display re-render at <c>(window_index+1) × rotation</c>, and
    /// nothing in the DOM announces that in advance. The predicate is evaluated immediately after each
    /// read, so a predicate that samples the clock is sampling it after the observation rather than
    /// before — which is what keeps "is this code live?" from racing the boundary it is asking about.</para>
    /// </summary>
    /// <param name="expectation">
    /// Completes the sentence "the table display did not show …". A timeout here is the most likely way
    /// either scenario fails, so the message has to say what was being waited for and what was on screen
    /// instead.
    /// </param>
    internal static async Task<string> WaitForJoinQrPathAsync(
        IPage page,
        Func<string, bool> isAcceptable,
        TimeSpan timeout,
        string expectation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(isAcceptable);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string? lastObserved = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                lastObserved = await ReadJoinQrPathAsync(page);

                if (isAcceptable(lastObserved))
                {
                    return lastObserved;
                }
            }
            catch (PlaywrightException)
            {
                // The surface re-rendered between the locator resolving and the attribute read, or the
                // code is not on screen yet. Both are ordinary; try again until the deadline.
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The table display did not show {expectation} within {timeout.TotalSeconds:F0}s."
            + $" What was on screen last: {Fingerprint(lastObserved)}."));
    }

    /// <summary>
    /// A short, quotable stand-in for a QR path, which runs to a couple of thousand characters and is
    /// unreadable in full. Two different codes practically never share both a length and a tail.
    /// </summary>
    internal static string Fingerprint(string? joinQrPath)
    {
        if (joinQrPath is null)
        {
            return "no QR at all";
        }

        string tail = joinQrPath.Length <= 24 ? joinQrPath : joinQrPath[^24..];

        return string.Create(
            CultureInfo.InvariantCulture,
            $"a path of {joinQrPath.Length} characters ending '{tail}'");
    }

    private static bool IsTableDisplayUrl(string url) => TryReadTableIdentifier(url, out _);

    /// <summary>
    /// Reads the table identifier out of a <c>/display/{table}</c> URL. <c>/display/pair</c> fails the
    /// <see cref="Guid.TryParse(string, out Guid)"/>, which is exactly what makes this usable as the
    /// "have we left the pairing page?" predicate as well.
    /// </summary>
    private static bool TryReadTableIdentifier(string url, out Guid tableIdentifier)
    {
        tableIdentifier = Guid.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string prefix = DisplayRoutes.Prefix + "/";
        string path = parsed.AbsolutePath;

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParse(path[prefix.Length..], out tableIdentifier);
    }

    private static async Task<string> DescribeRefusalAsync(IPage page)
    {
        ILocator refusals = page.Locator("p.status-error");

        if (await refusals.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The browser is at '{page.Url}' and the page shows no refusal.");
        }

        string message = (await refusals.First.InnerTextAsync()).Trim();

        return string.Create(CultureInfo.InvariantCulture, $"The page refused it: {message}");
    }
}
