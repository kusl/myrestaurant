using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class EnhancedNavigation
{
    internal static async Task FollowAsync(
        IPage page,
        ILocator link,
        string arrivalSelector,
        string surfaceDescription,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(link);

        string origin = page.Url;

        await link.ClickAsync();

        try
        {
            await page.Locator(arrivalSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,

                    $"Following a link away from '{origin}' never produced {surfaceDescription}"
                    + $" ('{arrivalSelector}') within {timeout.TotalSeconds:F0}s. The browser is now at"
                    + $" '{page.Url}' — but under enhanced navigation that address was pushed onto the"
                    + $" history before the page was fetched, so it is where the browser was heading"
                    + $" rather than where it arrived. Either the destination renders something other"
                    + $" than this selector, or the fetch behind it failed."),
                exception);
        }
    }
}
