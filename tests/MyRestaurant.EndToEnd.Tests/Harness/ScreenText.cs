using System.Text;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// Reading the text a component <em>declared</em>, rather than the text a browser <em>painted</em>.
///
/// <para><b>Why this exists.</b> Playwright's <c>InnerTextAsync</c> returns rendered text — the browser's
/// own <c>HTMLElement.innerText</c>, which is defined in terms of layout and therefore has
/// <c>text-transform</c> already applied to it. An element styled
/// <c>text-transform: uppercase</c> whose markup says <c>Settled total</c> reads back as
/// <c>SETTLED TOTAL</c>, and a harness comparing that against the phrase in the component fails on a
/// stylesheet rather than on the application.</para>
///
/// <para>That is not a hypothetical. M6 Slice 13 asserted <c>"Settled total"</c> against
/// <c>.counter-detail-total-label</c>, which <c>CounterSitting.razor</c> upcases for the eyebrow
/// treatment, and §16.3 scenario 10 went red on the first character. Forty lines further on,
/// <c>ReadTotalsAsync</c> was about to look <c>"Your total"</c> up in a dictionary keyed on the same
/// element's rendered text, with <c>.order-totals dt</c> upcased in <c>app.css</c> — a second instance
/// of the same mistake that the first one was hiding.</para>
///
/// <para><b>Which reads belong here and which do not.</b> The distinction is what the comparison is
/// about. A <em>label</em> read — one where the harness holds the phrase the component is expected to
/// have chosen, such as \"Running total\" against \"Settled total\" — is a claim about the component's
/// branch, and presentation casing is noise in it. A read of <em>content</em> — a table's label, a
/// person's name, an amount through <c>MoneyText.Format</c> — is data that no stylesheet in this
/// application transforms, and <c>InnerTextAsync</c>'s whitespace normalisation is genuinely convenient
/// there. So this is deliberately not a blanket replacement: it is used at the label sites, and the
/// sixty-odd content reads elsewhere are left alone.</para>
///
/// <para><b>Why the whitespace has to be collapsed here.</b> <c>textContent</c> is the raw concatenation
/// of the descendant text nodes, newlines and indentation included, where <c>innerText</c> has already
/// been through layout and comes back tidy. Razor's default whitespace handling removes whitespace-only
/// nodes that lead or trail inside an element, so in practice most of these read cleanly — but
/// \"in practice\" is not a thing to assert on, and collapsing runs to a single space makes the result
/// identical to what <c>InnerTextAsync</c> would have produced minus the transform. That is the whole
/// intended difference between the two.</para>
/// </summary>
internal static class ScreenText
{
    /// <summary>
    /// The text <paramref name="locator"/>'s markup declares, with runs of whitespace collapsed to a
    /// single space and the ends trimmed.
    ///
    /// <para>An element that exists and holds nothing reads as the empty string. A locator matching
    /// nothing throws from Playwright, which is the right outcome: every caller here is reading a label
    /// it has already established should be on screen, and \"the element is missing\" is a different
    /// failure from \"the label says something else\".</para>
    /// </summary>
    internal static async Task<string> DeclaredAsync(ILocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        return Collapse(await locator.TextContentAsync());
    }

    /// <summary>
    /// Runs of whitespace to a single space, ends trimmed, <c>null</c> to the empty string.
    ///
    /// <para>Separate from <see cref="DeclaredAsync"/> and not private, because a caller that has
    /// already read a string some other way — an attribute, a value, a sentence assembled from two
    /// elements — should be able to normalise it the same way rather than approximately.</para>
    /// </summary>
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
                // Deferred rather than appended: a trailing run then costs nothing to remove, and the
                // leading one is never written at all because nothing has been appended to space from.
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
