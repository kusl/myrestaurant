using System.Globalization;
using MyRestaurant.Domain.Security;
using Net.Codecrete.QrCodeGenerator;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// Turns the QR code a table display has on screen into a sentence about <em>which window it belongs
/// to</em> (TECHNICAL_SPECIFICATION §4.3, §11.5).
///
/// <para><b>Why this exists at all.</b> §16.3 scenario 2 requires the display's QR to <em>change across a
/// window boundary</em>, and scenario 15 requires the display's <em>next window</em> to work after the
/// join secret is rotated. The screen renders nothing but an inline SVG — no token text, no URL — so
/// "different pixels" is the only thing a browser can observe directly, and "different pixels" is a
/// weak assertion: a display frozen on a stale code, or one signed by the wrong secret, would satisfy
/// it just as happily as a healthy one. What actually needs proving is that the artefact on screen is
/// the code the server would <em>accept right now</em>.</para>
///
/// <para><b>How.</b> The join secret is read straight out of the row (the harness can, §4.1 only forbids
/// the <em>application</em> from letting it out), the token comes from the domain's own
/// <see cref="JoinTokenService"/>, the URL from its own <see cref="JoinTokenService.BuildJoinUrl"/>, and
/// the module geometry from the same <c>Net.Codecrete.QrCodeGenerator</c> call the renderer makes. So the
/// expected path is computed independently of the web layer, but from exactly the inputs and library the
/// web layer uses.</para>
///
/// <para><b>The one duplication, named.</b> Three facts about
/// <c>TableJoinTokens.RenderJoinQrSvg</c> are restated here: error-correction level Medium, a
/// four-module quiet zone, and <c>ToGraphicsPath</c> as the source of the <c>d</c> attribute. They are
/// private in the web layer and should stay private — nothing in the product needs them, and widening
/// their visibility to satisfy a test would be the worse trade. If any of the three ever changes, both
/// scenarios fail immediately and say so, which is the behaviour a duplicated constant should have.</para>
/// </summary>
internal static class JoinQrCodes
{
    /// <summary>The quiet zone <c>TableJoinTokens.RenderJoinQrSvg</c> bakes into the path (§4.3).</summary>
    internal const int QuietZoneModules = 4;

    /// <summary>
    /// How far back <see cref="Classify"/> looks before giving up. Two windows further than §4.3's live
    /// pair, so a stale display is reported as <em>how</em> stale rather than as unrecognisable — the
    /// difference between "the refresh loop is behind" and "this is not even this table's code".
    /// </summary>
    internal const int DefaultLookbehindWindows = 3;

    /// <summary>The two classifications §4.3 accepts: "the current and previous window".</summary>
    internal static readonly IReadOnlyList<string> LiveAges = [JoinQrAge.Current, JoinQrAge.Previous];

    /// <summary>
    /// The <c>d</c> attribute the display's QR <c>&lt;path&gt;</c> must carry for one window's token.
    /// </summary>
    internal static string PathFor(
        byte[] joinSecret,
        Guid tableIdentifier,
        string publicOrigin,
        long windowIndex)
    {
        ArgumentNullException.ThrowIfNull(joinSecret);

        string token = JoinTokenService.ComputeToken(joinSecret, tableIdentifier, windowIndex);
        string joinUrl = JoinTokenService.BuildJoinUrl(publicOrigin, tableIdentifier, token);

        return QrCode.EncodeText(joinUrl, QrCode.Ecc.Medium).ToGraphicsPath(QuietZoneModules);
    }

    /// <summary>
    /// Which window the observed path belongs to, as a phrase fit for an assertion message. Searching
    /// backwards from <paramref name="newestWindowIndex"/> is deliberate: the server rendered at or
    /// before the moment the browser was read, never after it, so a future window is not a possibility
    /// worth considering — while a past one is exactly the failure this is here to catch.
    /// </summary>
    internal static string Classify(
        string observedQrPath,
        byte[] joinSecret,
        Guid tableIdentifier,
        string publicOrigin,
        long newestWindowIndex,
        int lookbehindWindows = DefaultLookbehindWindows)
    {
        for (int offset = 0; offset <= lookbehindWindows; offset++)
        {
            string candidate = PathFor(joinSecret, tableIdentifier, publicOrigin, newestWindowIndex - offset);

            if (string.Equals(observedQrPath, candidate, StringComparison.Ordinal))
            {
                return offset switch
                {
                    0 => JoinQrAge.Current,
                    1 => JoinQrAge.Previous,
                    _ => string.Create(CultureInfo.InvariantCulture, $"a code {offset} windows out of date"),
                };
            }
        }

        return JoinQrAge.Unrecognised;
    }

    /// <summary>
    /// True when the observed path is the current or previous window's code for this secret — §4.3's
    /// definition of a code that still validates. Reads its own window index from
    /// <paramref name="observedAt"/>, which the caller should sample <em>after</em> reading the browser.
    /// </summary>
    internal static bool IsLive(
        string observedQrPath,
        byte[] joinSecret,
        Guid tableIdentifier,
        string publicOrigin,
        DateTimeOffset observedAt,
        int rotationSeconds)
    {
        long newestWindowIndex = JoinTokenService.CurrentWindowIndex(observedAt, rotationSeconds);

        string age = Classify(
            observedQrPath,
            joinSecret,
            tableIdentifier,
            publicOrigin,
            newestWindowIndex,
            lookbehindWindows: 1);

        return LiveAges.Contains(age);
    }
}

/// <summary>
/// The vocabulary <see cref="JoinQrCodes.Classify"/> answers in. Phrases rather than an enum so that a
/// failed <c>Assert.Contains</c> reads as English — the whole point of classifying instead of comparing
/// two thousand characters of SVG path.
/// </summary>
internal static class JoinQrAge
{
    /// <summary>The code for the window in force at the moment of observation.</summary>
    internal const string Current = "the current window's code";

    /// <summary>The immediately previous window's code — still valid per §4.3.</summary>
    internal const string Previous = "the previous window's code";

    /// <summary>Not produced by this table's join secret for any window recently in play.</summary>
    internal const string Unrecognised = "a code this table's join secret does not produce";
}
