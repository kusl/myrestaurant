using MyRestaurant.Domain.Menu;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class ImageFormatTests
{
    private static readonly byte[] PngPrefix =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegPrefix = [0xFF, 0xD8, 0xFF, 0xE0];

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

    [Fact]
    public void ARiffContainerThatIsNotWebPIsNotIdentified()
    {
        Assert.Null(ImageFormat.IdentifyContentType(Riff("AVI ")));
        Assert.Null(ImageFormat.IdentifyContentType(Riff("WAVE")));
    }

    [Fact]
    public void AFormatThisApplicationDoesNotServeIsNotIdentified()
    {
        Assert.Null(ImageFormat.IdentifyContentType(
            [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]));

        Assert.Null(ImageFormat.IdentifyContentType([0x42, 0x4D, 0x00, 0x00]));

        Assert.Null(ImageFormat.IdentifyContentType(
            [0x3C, 0x73, 0x76, 0x67, 0x20, 0x78, 0x6D, 0x6C, 0x6E, 0x73]));

        Assert.Null(ImageFormat.IdentifyContentType([0x25, 0x50, 0x44, 0x46, 0x2D]));
    }

    [Fact]
    public void BytesTooShortToCarryASignatureAreNotIdentified()
    {
        Assert.Null(ImageFormat.IdentifyContentType([]));
        Assert.Null(ImageFormat.IdentifyContentType([0x89]));
        Assert.Null(ImageFormat.IdentifyContentType(PngPrefix.AsSpan(0, 7)));

        Assert.Null(ImageFormat.IdentifyContentType(
            [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45]));
    }

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

        Assert.False(ImageFormat.BytesMatchDeclaredContentType(PngPrefix, "IMAGE/PNG"));
    }

    [Fact]
    public void EveryRecognisedContentTypeIsOneSomeBytesProduce()
    {
        Assert.NotEmpty(ImageFormat.RecognisedContentTypes);

        Assert.Equal(
            ImageFormat.RecognisedContentTypes.Count,
            ImageFormat.RecognisedContentTypes.Distinct(StringComparer.Ordinal).Count());

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
