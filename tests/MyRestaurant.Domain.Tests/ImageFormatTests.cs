using MyRestaurant.Domain.Menu;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Unit tests for <see cref="ImageFormat"/> — deciding what a run of bytes actually is
/// (TECHNICAL_SPECIFICATION §7, §8.2).
///
/// <para><b>Why these are unit tests and not integration facts (F-100).</b> The subject is a pure function
/// of a byte span, and the cases that matter are the malformed ones: a truncated signature, a RIFF
/// container that is not a WebP, a format this application refuses to serve. Behind an <c>INSERT</c> each
/// of those would cost a PostgreSQL container and a transaction to observe, and the interesting half —
/// <em>which</em> refusal — would arrive as a constraint name. Here each is a named assertion that runs in
/// the fast suite.</para>
///
/// <para><b><see cref="ARiffContainerThatIsNotWebPIsNotIdentified"/> is the one worth reading.</b> WebP is
/// a RIFF form, and the naive check is the four bytes <c>RIFF</c> at the start — which an AVI, a WAV and
/// several other things also open with. A signature check that stopped there would accept an audio file as
/// a picture, and §7's route would then serve it from this application's own origin with
/// <c>Content-Type: image/webp</c>. Every other fact in this file passes on that implementation.</para>
///
/// <para><b>No sample file is committed and none is needed.</b> A signature is a prefix, so the shortest
/// honest arrangement is the prefix followed by whatever — which is what these facts build. That is a
/// deliberate limit on what this file claims: it asserts what the first bytes say, not that the remainder
/// is a decodable image, because nothing in this stack decodes one (§7).</para>
/// </summary>
public sealed class ImageFormatTests
{
    private static readonly byte[] PngPrefix =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegPrefix = [0xFF, 0xD8, 0xFF, 0xE0];

    /// <summary>
    /// <c>RIFF</c>, a four-byte little-endian length, then the form. The length is arbitrary here: nothing
    /// in <see cref="ImageFormat"/> reads it, which is itself a decision — a length that disagreed with the
    /// upload would be a property of a file this application never parses.
    /// </summary>
    private static byte[] Riff(string form, params byte[] tail)
        =>
        [
            0x52, 0x49, 0x46, 0x46,
            0x00, 0x00, 0x00, 0x00,
            (byte)form[0], (byte)form[1], (byte)form[2], (byte)form[3],
            .. tail,
        ];

    [Fact]
    public void PngIsIdentifiedFromItsEightByteSignature()
    {
        Assert.Equal(
            ImageFormat.PngContentType,
            ImageFormat.IdentifyContentType([.. PngPrefix, 0x00, 0x00, 0x00, 0x0D]));
    }

    [Fact]
    public void JpegIsIdentifiedFromItsStartOfImageMarker()
    {
        Assert.Equal(
            ImageFormat.JpegContentType,
            ImageFormat.IdentifyContentType([.. JpegPrefix, 0x00, 0x10, 0x4A, 0x46]));
    }

    [Fact]
    public void WebPIsIdentifiedFromBothHalvesOfItsRiffHeader()
    {
        Assert.Equal(
            ImageFormat.WebPContentType,
            ImageFormat.IdentifyContentType(Riff("WEBP", 0x56, 0x50, 0x38, 0x20)));
    }

    /// <summary>
    /// The case the naive implementation gets wrong. Both of these open with the same four bytes a WebP
    /// does, and neither is a picture.
    /// </summary>
    [Fact]
    public void ARiffContainerThatIsNotWebPIsNotIdentified()
    {
        Assert.Null(ImageFormat.IdentifyContentType(Riff("AVI ")));
        Assert.Null(ImageFormat.IdentifyContentType(Riff("WAVE")));
    }

    /// <summary>
    /// Formats a browser would happily render and this application will not store, plus one that is not an
    /// image at all. GIF is the interesting member: it is a perfectly good picture and it is excluded by
    /// §8.2's vocabulary rather than by being unrecognisable, so a future decision to accept it is a
    /// migration and an entry here rather than a silent widening.
    /// </summary>
    [Fact]
    public void AFormatThisApplicationDoesNotServeIsNotIdentified()
    {
        // GIF89a
        Assert.Null(ImageFormat.IdentifyContentType(
            [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]));

        // BM — a Windows bitmap.
        Assert.Null(ImageFormat.IdentifyContentType([0x42, 0x4D, 0x00, 0x00]));

        // "<svg xmlns" — an image that is a document, and the one whose being served from this origin
        // would matter most, §11.11's policy notwithstanding.
        Assert.Null(ImageFormat.IdentifyContentType(
            [0x3C, 0x73, 0x76, 0x67, 0x20, 0x78, 0x6D, 0x6C, 0x6E, 0x73]));

        // %PDF-
        Assert.Null(ImageFormat.IdentifyContentType([0x25, 0x50, 0x44, 0x46, 0x2D]));
    }

    /// <summary>
    /// Nothing, one byte, and a PNG signature one byte short. The last is the one a length check off by
    /// one would let through, and the empty case is the one the write refuses before it ever gets here —
    /// asserted anyway, because a caller that skipped that guard must get a refusal rather than an
    /// exception.
    /// </summary>
    [Fact]
    public void BytesTooShortToCarryASignatureAreNotIdentified()
    {
        Assert.Null(ImageFormat.IdentifyContentType([]));
        Assert.Null(ImageFormat.IdentifyContentType([0x89]));
        Assert.Null(ImageFormat.IdentifyContentType(PngPrefix.AsSpan(0, 7)));

        // RIFF and its length, with the form field truncated away: the offset check, not the prefix.
        Assert.Null(ImageFormat.IdentifyContentType(
            [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45]));
    }

    /// <summary>
    /// The declared-versus-actual comparison the write actually calls, in both directions and with the two
    /// degenerate arguments. The third assertion is the finding this whole type exists for: bytes that are
    /// a real picture of the wrong format are still a mismatch, because §7's route hands the stored column
    /// back as the response header.
    /// </summary>
    [Fact]
    public void BytesMatchTheirDeclaredTypeOnlyWhenTheyAreThatType()
    {
        Assert.True(ImageFormat.BytesMatchDeclaredContentType(
            PngPrefix, ImageFormat.PngContentType));

        Assert.False(ImageFormat.BytesMatchDeclaredContentType(
            PngPrefix, ImageFormat.JpegContentType));

        Assert.False(ImageFormat.BytesMatchDeclaredContentType(
            Riff("AVI "), ImageFormat.WebPContentType));

        Assert.False(ImageFormat.BytesMatchDeclaredContentType(PngPrefix, null));
        Assert.False(ImageFormat.BytesMatchDeclaredContentType([], ImageFormat.PngContentType));

        // Ordinal, on purpose: a caller that has not normalised its input is a caller with a defect, not a
        // case to absorb silently.
        Assert.False(ImageFormat.BytesMatchDeclaredContentType(PngPrefix, "IMAGE/PNG"));
    }

    /// <summary>
    /// The non-vacuity guard, and the one that makes <see cref="ImageFormat.RecognisedContentTypes"/> a
    /// derived census rather than a maintained list (F-41): the set is non-empty, holds no duplicates, and
    /// <b>every member is reachable</b> — there are bytes that produce it. A name added to that list
    /// without a signature to match fails here, which is the half a list on its own can never promise.
    /// </summary>
    [Fact]
    public void EveryRecognisedContentTypeIsOneSomeBytesProduce()
    {
        Assert.NotEmpty(ImageFormat.RecognisedContentTypes);

        Assert.Equal(
            ImageFormat.RecognisedContentTypes.Count,
            ImageFormat.RecognisedContentTypes.Distinct(StringComparer.Ordinal).Count());

        // Coalesced to a word rather than asserted non-null one at a time, so that a name on the list
        // with no signature behind it fails the comparison below with a readable value in the message
        // instead of a null reference three lines earlier.
        string[] produced =
        [
            ImageFormat.IdentifyContentType(PngPrefix) ?? "unrecognised",
            ImageFormat.IdentifyContentType(JpegPrefix) ?? "unrecognised",
            ImageFormat.IdentifyContentType(Riff("WEBP")) ?? "unrecognised",
        ];

        Assert.Equal(
            ImageFormat.RecognisedContentTypes.Order(StringComparer.Ordinal).ToArray(),
            produced.Order(StringComparer.Ordinal).ToArray());
    }
}
