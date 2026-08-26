using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.Domain.Security;
using MyRestaurant.WebApplication.Identity;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class TableJourneys
{
    internal const string ExpiredHeading = "That code has expired";

    internal enum JoinStage
    {
        Expired,
        Confirm,
        Member,
        SentToSignIn,
    }

    internal static async Task<JoinStage> ScanAsync(IPage page, Guid tableIdentifier, string token)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(JoinPath(tableIdentifier, token));

        return await JoinStageOnScreen(page);
    }

    internal static string JoinPath(Guid tableIdentifier, string token)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"/table/{tableIdentifier:D}?token={Uri.EscapeDataString(token)}");

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

                    $"Joining did not confirm; the table page is now showing {stage}. A grant is"
                    + $" single-use and is cleared whatever the outcome (§4.4), so if this was a"
                    + $" refusal the grant is already spent and a retry will not help."),
                exception);
        }
    }

    internal static async Task<IPage> SeatGuestAsync(
        RestaurantInstance instance,
        Guid tableIdentifier,
        byte[] joinSecret,
        GuestAccount account,
        TimeSpan patience,
        CancellationToken cancellationToken,
        bool handheld = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(joinSecret);
        ArgumentNullException.ThrowIfNull(account);

        string token = JoinTokenService.ComputeCurrentToken(
            joinSecret, tableIdentifier, DateTimeOffset.UtcNow, instance.TableJoinTokenRotationSeconds);

        IPage guest = await instance.OpenIsolatedPageAsync(
            withVirtualAuthenticator: true, handheld: handheld);

        JoinStage afterScan = await ScanAsync(guest, tableIdentifier, token);

        if (afterScan is not JoinStage.SentToSignIn)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A fresh scan of table {tableIdentifier:D} resolved to {afterScan} rather than"
                    + $" {JoinStage.SentToSignIn}. An anonymous scanner holding a live token is sent to"
                    + $" sign in (§4.4), so this is a dead token or a table nobody can join."));
        }

        await AccountJourneys.RegisterGuestWithPasskeyAsync(guest, account);
        await JoinAsync(guest);
        await TableOrderJourneys.WaitForLiveSurfaceAsync(guest, patience);

        cancellationToken.ThrowIfCancellationRequested();

        return guest;
    }

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

        return await page.Locator("form button[type='submit']:has-text('Join')").CountAsync() > 0
            ? JoinStage.Confirm
            : JoinStage.Member;
    }
}
