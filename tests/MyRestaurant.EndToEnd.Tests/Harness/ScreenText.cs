using System.Text;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class ScreenText
{
    internal static async Task<string> DeclaredAsync(ILocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        return Collapse(await locator.TextContentAsync());
    }

    internal static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder collapsed = new(text.Length);
        bool pendingSpace = false;

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(character);
        }

        return collapsed.ToString();
    }
}
