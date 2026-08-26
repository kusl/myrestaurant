using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class MenuPictureJourneys
{
    internal const string FileInput = "#picture-file";

    internal const string Status = "#picture-status";

    internal const string Flash = ".status-success";

    internal const string Thumbnail = "img.manage-picture-image";

    internal const string AltTextInput = "#picture-alt-text";

    internal const string Facts = "figcaption.manage-picture-facts";

    private const float UploadPatienceMilliseconds = 60_000;

    internal static async Task<int> AttachAsync(
        IPage administrator,
        MenuItemOnTheMenu item,
        byte[] picture,
        string fileName = "dish.png",
        string mimeType = "image/png",
        string? caption = null)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(picture);

        await administrator.GotoAsync($"/administration/menu/{item.Identifier:D}");

        await administrator.SetInputFilesAsync(
            FileInput,
            new FilePayload
            {
                Name = fileName,
                MimeType = mimeType,
                Buffer = picture,
            });

        await SettleAsync(administrator);

        await administrator.ClickAsync("button:has-text('Attach picture')");

        try
        {
            await administrator.Locator(Flash).WaitForAsync(
                new LocatorWaitForOptions { Timeout = UploadPatienceMilliseconds });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                $"Attaching a picture to '{item.Name}' never reached a success flash. Either the POST"
                    + " was refused, or the page the redirect landed on threw — which is F-106's exact"
                    + " shape, and is why this waits on the flash rather than on the navigation.",
                exception);
        }

        if (caption is not null)
        {
            await administrator.FillAsync(AltTextInput, caption);
            await administrator.ClickAsync("button:has-text('Save caption')");

            await administrator.Locator(Flash).WaitForAsync(
                new LocatorWaitForOptions { Timeout = UploadPatienceMilliseconds });
        }

        return await WaitForDecodedAsync(administrator, Thumbnail);
    }

    internal static async Task<int> WaitForDecodedAsync(IPage page, string selector)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(selector);

        ILocator images = page.Locator(selector);

        await images.First.WaitForAsync(
            new LocatorWaitForOptions { Timeout = UploadPatienceMilliseconds });

        try
        {
            await page.WaitForFunctionAsync(
                DecodedScript,
                selector,
                new PageWaitForFunctionOptions { Timeout = UploadPatienceMilliseconds });
        }
        catch (PlaywrightException exception)
        {
            int found = await images.CountAsync();

            throw new InvalidOperationException(
                $"'{selector}' matched {found} element(s) and at least one never decoded. The markup"
                    + " carries a src and the browser could not turn it into pixels — §7's route"
                    + " answered something other than the stored bytes, the stored content type"
                    + " disagrees with them, or §11.11's img-src refused the origin. An undecoded"
                    + " <img> still has a box, so anything measuring this element would have measured a"
                    + " placeholder and reported it fine.",
                exception);
        }

        return await images.First.EvaluateAsync<int>("element => element.naturalWidth");
    }

    private const string DecodedScript = """
        (selector) => {
            const images = Array.from(document.querySelectorAll(selector));
            return images.length > 0 && images.every((image) => image.naturalWidth > 0);
        }
        """;

    internal static Task SettleAsync(IPage page)
        => page.WaitForFunctionAsync(
            SettledScript,
            null,
            new PageWaitForFunctionOptions { Timeout = UploadPatienceMilliseconds });

    private static readonly string SettledScript =
        "() => { const status = document.querySelector('" + Status + "');"
        + " const control = document.querySelector('" + FileInput + "');"
        + " return status !== null && control !== null && !control.disabled"
        + " && status.textContent.length > 0 && status.textContent.indexOf('Resizing') < 0; }";
}
