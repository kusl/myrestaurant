using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Displays;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class DisplayJourneys
{
    private const string JoinQrPathSelector = "#table-display-surface svg.join-qr-svg path";

    private const string SurfaceSelector = "#table-display-surface";

    private const string LiveSurfaceSelector =
        "#table-display-surface[data-live='true'][data-loaded='true']";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<Guid> PairAsync(IPage page, string pairingCode, string deviceLabel)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(DisplayRoutes.Pair);

        await page.FillAsync("#pairing-code", pairingCode);
        await page.FillAsync("#device-label", deviceLabel);
        await page.ClickAsync("button:has-text('Pair this display')");

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

    internal static async Task WaitForLiveSurfaceAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(LiveSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,

                    $"The table display was not live and showing a code within"
                    + $" {timeout.TotalSeconds:F0}s ({surface}). A surface present with"
                    + $" data-live='false' is still the prerendered markup: nothing on the page will"
                    + $" ever change — the QR cannot advance across a rotation boundary and the"
                    + $" party-size chip cannot move — because no Blazor circuit was established. Check"
                    + $" that /_framework/blazor.web.js is served (RestaurantInstance probes it at"
                    + $" startup) and that the browser reached /_blazor. One live but stuck at"
                    + $" data-loaded='false' is the “Preparing the join code…” card: the circuit is"
                    + $" there and §4.3 returned no code for this table, so look at the table's join"
                    + $" secret and at whether the row is still active."),
                exception);
        }
    }

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
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The table display did not show {expectation} within {timeout.TotalSeconds:F0}s."
            + $" What was on screen last: {Fingerprint(lastObserved)}."));
    }

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

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(SurfaceSelector).First;

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no display surface on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.GetAttributeAsync("data-live");
        string? loaded = await surface.GetAttributeAsync("data-loaded");
        string? token = await surface.GetAttributeAsync("data-refresh-token");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"data-live='{live ?? "absent"}', data-loaded='{loaded ?? "absent"}',"
            + $" data-refresh-token='{token ?? "absent"}'");
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
