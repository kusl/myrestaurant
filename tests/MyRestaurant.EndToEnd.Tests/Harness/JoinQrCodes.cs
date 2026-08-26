using System.Globalization;
using MyRestaurant.Domain.Security;
using Net.Codecrete.QrCodeGenerator;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class JoinQrCodes
{
    internal const int QuietZoneModules = 4;

    internal const int DefaultLookbehindWindows = 3;

    internal static readonly IReadOnlyList<string> LiveAges = [JoinQrAge.Current, JoinQrAge.Previous];

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

internal static class JoinQrAge
{
    internal const string Current = "the current window's code";

    internal const string Previous = "the previous window's code";

    internal const string Unrecognised = "a code this table's join secret does not produce";
}
