namespace MyRestaurant.Domain.Menu;

/// <summary>
/// What format a run of bytes actually is, decided from the bytes themselves
/// (TECHNICAL_SPECIFICATION §7, §8.2).
///
/// <para><b>Why this exists at all.</b> A menu image arrives from a browser with a declared
/// <c>Content-Type</c>, and that declaration is the client's claim about its own upload. §8.2 stores the
/// claim in a column and §7's route hands it straight back out as the response's <c>Content-Type</c>, so a
/// column that disagreed with its own bytes would make this application serve a JPEG labelled as a PNG,
/// or an arbitrary file labelled as an image, on its own origin. This type is what turns the claim into
/// something checked: the write compares what the caller declared against what the bytes say, and refuses
/// the pair when they disagree.</para>
///
/// <para><b>It is here rather than in DataAccess, and that is the same argument
/// <c>MenuGrouping</c>, <c>KitchenQueue</c>, <c>OrderStaging</c> and <c>OrderNarrative</c> already
/// carry (F-100).</b> Deciding a format from a signature is a pure function of a byte span — no clock, no
/// connection, no container — so putting it beside the write would make every fact about it cost a
/// PostgreSQL container and two and a half seconds, and would put the interesting cases (a truncated
/// signature, a RIFF container that is not a WebP) behind an INSERT. Domain references nothing but the
/// BCL (§2), which this file honours: it reads bytes and returns a string.</para>
///
/// <para><b>What it deliberately does not do: decode, resize, re-encode or measure.</b> There is no
/// free-libre .NET image library available to this stack for this use — ImageSharp's licence does not
/// admit it and SkiaSharp is a native dependency inside a rootless container — so nothing here opens the
/// image. It reads the first few bytes and no more. That bound is the reason §8.2 stores no
/// <c>pixel_width</c> and no <c>pixel_height</c>: a dimension nothing in this stack can measure would be
/// the client's unverifiable claim stored in the indicative, which is a worse artefact than an absent
/// column (F-101).</para>
///
/// <para><b>The recognised set is the vocabulary §8.2's CHECK admits, and the agreement is asserted
/// behaviourally rather than by comparing two lists.</b> This is F-80's shape — a vocabulary declared in
/// a migration with a copy in C# — and the repair there was a gate that read the SQL text. The gate here
/// is stronger: <c>MenuItemImageTests</c> attaches a real file of every format
/// <see cref="RecognisedContentTypes"/> names and requires the database to accept it, so the two agreeing
/// on paper while nothing can actually be stored is also a failure.</para>
/// </summary>
public static class ImageFormat
{
    /// <summary>The stored spelling of a PNG's media type.</summary>
    public const string PngContentType = "image/png";

    /// <summary>The stored spelling of a JPEG's media type.</summary>
    public const string JpegContentType = "image/jpeg";

    /// <summary>The stored spelling of a WebP's media type.</summary>
    public const string WebPContentType = "image/webp";

    /// <summary>
    /// The eight bytes every PNG opens with (the signature in the specification's own words: a
    /// high-bit byte, the letters P N G, a CRLF, a DOS end-of-file and a bare LF — chosen so that a
    /// transfer which mangled line endings could be detected).
    /// </summary>
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// A JPEG's start-of-image marker and the first byte of whatever marker follows it. Three bytes
    /// rather than two: <c>FF D8</c> alone is also the opening of nothing else in practice, but every
    /// JPEG's third byte is <c>FF</c> because a marker follows immediately, and requiring it is free.
    /// </summary>
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    /// <summary>The RIFF container's own four bytes, which WebP shares with several other formats.</summary>
    private static readonly byte[] RiffSignature = [0x52, 0x49, 0x46, 0x46];

    /// <summary>
    /// The four bytes at offset 8 that say which RIFF form this is. <b>Both halves are required and
    /// that is the case most easily got wrong:</b> a RIFF header alone is an AVI, a WAV or a dozen other
    /// things, so a check that stopped at <see cref="RiffSignature"/> would accept an audio file as an
    /// image and this application would serve it with <c>Content-Type: image/webp</c>.
    /// </summary>
    private static readonly byte[] WebPForm = [0x57, 0x45, 0x42, 0x50];

    /// <summary>Where <see cref="WebPForm"/> sits: after RIFF's four bytes and its four-byte length.</summary>
    private const int WebPFormOffset = 8;

    /// <summary>
    /// Every media type this application recognises and is therefore willing to store and serve. It is
    /// the derived census rather than a maintained one — each entry is a format
    /// <see cref="IdentifyContentType"/> can actually return, so a member nothing can produce is
    /// impossible, which is the half a list on its own cannot promise.
    /// </summary>
    public static IReadOnlyList<string> RecognisedContentTypes { get; } =
        [JpegContentType, PngContentType, WebPContentType];

    /// <summary>
    /// The media type these bytes are, or <c>null</c> when they are not one of the three formats this
    /// application serves. Empty and truncated input are <c>null</c> rather than an exception: the caller
    /// is a write handling an upload, and a run of bytes that is not an image is an ordinary refusal
    /// rather than a fault.
    /// </summary>
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

        // Both halves, in one condition, so no future edit can satisfy the first and forget the second.
        if (bytes.StartsWith(RiffSignature)
            && bytes.Length >= WebPFormOffset + WebPForm.Length
            && bytes.Slice(WebPFormOffset, WebPForm.Length).SequenceEqual(WebPForm))
        {
            return WebPContentType;
        }

        return null;
    }

    /// <summary>
    /// Whether these bytes are the format the uploader said they were — the question the write actually
    /// asks. Ordinal comparison: a media type is a token with one spelling, and <c>IMAGE/PNG</c> arriving
    /// from somewhere is a caller that has not normalised its input rather than a case this should
    /// silently accept.
    /// </summary>
    public static bool BytesMatchDeclaredContentType(
        ReadOnlySpan<byte> bytes,
        string? declaredContentType)
        => declaredContentType is not null
           && string.Equals(IdentifyContentType(bytes), declaredContentType, StringComparison.Ordinal);
}
