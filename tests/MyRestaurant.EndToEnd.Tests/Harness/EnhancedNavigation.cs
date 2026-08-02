using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// Following a link inside the application, in a way that survives Blazor's <em>enhanced
/// navigation</em>.
///
/// <para><b>Why this exists, in one sentence: the URL changes before the page does.</b> When
/// <c>blazor.web.js</c> is loaded and the page has no interactive <c>Router</c> — which is every
/// static-SSR surface in this application, so every account page, the join page and the display —
/// clicking an in-app link is intercepted. <c>NavigationEnhancement.ts</c>'s <c>onDocumentClick</c>
/// calls <c>history.pushState</c> with the destination and <em>then</em> starts the <c>fetch</c>, and
/// only when that resolves does <c>synchronizeDomContent</c> patch the new markup in. For the whole
/// round trip the address bar says one thing and the document says another.</para>
///
/// <para><b>Why that is a trap rather than a curiosity.</b> Playwright resolves
/// <c>WaitForURLAsync</c> on a same-document navigation the moment the URL matches — there is no
/// <c>load</c> event to wait for and none is coming. So a journey that waits on the URL and then
/// starts typing is typing into the <em>previous</em> page. That is harmless when the two surfaces
/// share no field names and silently destructive when they do: <c>DomSync.ts</c>'s
/// <c>ensureEditableValueSynchronized</c> assigns every input the value the server rendered, so
/// anything typed while the fetch was in flight is erased without a trace and the form posts empty.
/// §16.3 scenarios 3, 4 and 6 all failed this way — the sign-in page and the registration page both
/// have a <c>#username</c>, the username was wiped by the patch, and the details step refused itself
/// with "Choose a username." thirty seconds before the timeout said anything about it.</para>
///
/// <para><b>What makes the wait below sufficient.</b> <c>synchronizeDomContent</c> is a single
/// synchronous call on the main thread, and a Playwright query cannot interleave with it. So the
/// instant <em>any</em> part of the destination markup is observable, <em>all</em> of it is —
/// including the reset of every field the two surfaces have in common. Waiting for one element that
/// exists only on the destination is therefore an exact barrier, not a heuristic delay.</para>
///
/// <para>Form posts need none of this: <c>enhancedNavigationIsEnabledForForm</c> requires
/// <c>data-enhance</c> on the form element itself and nothing in this application sets it, so every
/// submit is an ordinary browser navigation and Playwright's usual waits mean what they say. Only
/// link clicks come through here.</para>
/// </summary>
internal static class EnhancedNavigation
{
    /// <summary>
    /// Clicks <paramref name="link"/> and returns once <paramref name="arrivalSelector"/> — an element
    /// the destination surface has and the current one does not — is on screen.
    /// </summary>
    /// <param name="page">The page holding the link.</param>
    /// <param name="link">The link to follow. Already located and waited for by the caller, so that a
    /// missing link fails with the caller's own sentence about what should have offered it.</param>
    /// <param name="arrivalSelector">
    /// Something only the destination renders. It must genuinely be absent from the page being left,
    /// or this returns immediately and the barrier is no barrier at all — that is the one way to get
    /// this wrong, and it is worth checking against the markup rather than assuming.
    /// </param>
    /// <param name="surfaceDescription">What that selector means, in words, for the failure message.</param>
    /// <param name="timeout">How long the destination has to arrive.</param>
    internal static async Task FollowAsync(
        IPage page,
        ILocator link,
        string arrivalSelector,
        string surfaceDescription,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(link);

        // Captured before the click: by the time this fails, page.Url is the destination the address
        // bar was optimistically moved to, and the origin is the part nobody can reconstruct.
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
                    // Every operand is interpolated on purpose. string.Create's second parameter is a
                    // `ref DefaultInterpolatedStringHandler`, and C# only converts an addition to a
                    // handler when the whole additive expression is composed of interpolated strings;
                    // one bare "…" literal makes the result a plain string and the call fails to bind
                    // with CS1620. A hole-less $"…" still counts.
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
