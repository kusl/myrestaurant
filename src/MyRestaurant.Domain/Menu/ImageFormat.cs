namespace MyRestaurant.Domain.Menu;

public static class ImageFormat
{
    public const string PngContentType = "image/png";

    public const string JpegContentType = "image/jpeg";

    public const string WebPContentType = "image/webp";

    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static readonly byte[] RiffSignature = [0x52, 0x49, 0x46, 0x46];

    private static readonly byte[] WebPForm = [0x57, 0x45, 0x42, 0x50];

    private const int WebPFormOffset = 8;

    public static IReadOnlyList<string> RecognisedContentTypes { get; } =
        [JpegContentType, PngContentType, WebPContentType];

    public static string? IdentifyContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature))
        {
            return PngContentType;
        }

        if (bytes.StartsWith(JpegSignature))
        {
            return JpegContentType;
        }

        if (bytes.StartsWith(RiffSignature)
            && bytes.Length >= WebPFormOffset + WebPForm.Length
            && bytes.Slice(WebPFormOffset, WebPForm.Length).SequenceEqual(WebPForm))
        {
            return WebPContentType;
        }

        return null;
    }

    public static bool BytesMatchDeclaredContentType(
        ReadOnlySpan<byte> bytes,
        string? declaredContentType)
        => declaredContentType is not null
           && string.Equals(IdentifyContentType(bytes), declaredContentType, StringComparison.Ordinal);
}
