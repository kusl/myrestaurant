using System.Buffers.Binary;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// A real PNG of a chosen size, built here (TECHNICAL_SPECIFICATION §7, §16.3; Stage 4e).
///
/// <para><b>Why the harness makes one rather than carrying one.</b> The plan recorded for four slices
/// that a picture scenario \u201cneeds a fixture image the harness has no way to produce, and inventing bytes
/// inside the harness would be a test arranging what it asserts about\u201d. Half of that is right and half of
/// it is not, and the difference is what this file is. Checking a photograph into the repository would be
/// an opaque blob nobody can reason about, and hard-coding a base64 string is the same blob with worse
/// ergonomics \u2014 but the objection about *arranging what it asserts* does not apply here, because nothing
/// downstream asserts anything about these bytes. The assertions are that an upload round-trips, that the
/// browser reduces one that is over \u00a78.2's cap, and that the history records it. The picture is the
/// arrangement, not the claim.</para>
///
/// <para><b>It is generated rather than compressed, and that is what makes the size a parameter.</b> The
/// pixels go into the PNG through <b>stored</b> deflate blocks \u2014 the format's uncompressed encoding \u2014 so
/// the file is very nearly <c>height \u00d7 (1 + width \u00d7 3)</c> bytes and a caller can ask for one comfortably
/// over the cap without guessing. A <c>ZLibStream</c> would have been three lines shorter and would have
/// compressed a smooth gradient to almost nothing, which is precisely the file this scenario cannot
/// use.</para>
///
/// <para><b>A gradient rather than noise, and it is load-bearing for the downscaling scenario.</b> The
/// browser re-encodes as JPEG, and JPEG's whole design is that smooth content is cheap and random content
/// is not: a raster of random pixels can survive every rung of the downscaler's ladder and still exceed
/// the cap, which would make the scenario fail for a reason that has nothing to do with the product. A
/// gradient is also what a photograph of a plate mostly is.</para>
///
/// <para><b>Truecolour, 8 bits, no filtering, no interlacing, no ancillary chunks.</b> The narrowest legal
/// PNG this can be, so that what a browser decodes is decided by the specification rather than by any
/// decoder's tolerance. The signature is the eight bytes
/// <c>MyRestaurant.Domain.Menu.ImageFormat</c> identifies, so the write service's own check passes on it
/// for the same reason a real PNG's does.</para>
/// </summary>
internal static class PictureFixtures
{
    /// <summary>The eight bytes every PNG opens with.</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The largest run a single stored deflate block may carry.</summary>
    private const int StoredBlockLimit = 0xFFFF;

    /// <summary>Adler-32's modulus.</summary>
    private const int AdlerModulus = 65521;

    /// <summary>
    /// A square gradient PNG <paramref name="edge"/> pixels on a side.
    ///
    /// <para>Square because nothing here is testing aspect handling and a caller reasoning about the
    /// resulting size should only have to think about one number. The byte length is deterministic:
    /// <c>edge \u00d7 (1 + edge \u00d7 3)</c> of pixel data, plus a little over one byte per 64 KiB of stored-block
    /// framing, plus about sixty bytes of chunk headers.</para>
    /// </summary>
    internal static byte[] SquareGradientPng(int edge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(edge, 1);

        byte[] raster = Raster(edge, edge);

        using MemoryStream png = new();
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], edge);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], edge);
        header[8] = 8;   // bit depth
        header[9] = 2;   // colour type: truecolour (RGB)
        header[10] = 0;  // compression method: deflate, the only one defined
        header[11] = 0;  // filter method: adaptive, the only one defined
        header[12] = 0;  // interlace method: none

        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, Deflate(raster));
        WriteChunk(png, "IEND"u8, []);

        return png.ToArray();
    }

    /// <summary>
    /// The raw scanlines: a filter-type byte per row followed by RGB triples, with each channel a
    /// straight ramp so the whole image is one smooth gradient.
    /// </summary>
    private static byte[] Raster(int width, int height)
    {
        byte[] raster = new byte[height * (1 + (width * 3))];
        int offset = 0;

        int widthSpan = Math.Max(1, width - 1);
        int heightSpan = Math.Max(1, height - 1);
        int diagonalSpan = Math.Max(1, width + height - 2);

        for (int y = 0; y < height; y++)
        {
            // Filter type 0 (None) on every row. A filter would make the bytes smaller under a real
            // compressor and changes nothing here, where the blocks are stored.
            raster[offset++] = 0;

            for (int x = 0; x < width; x++)
            {
                raster[offset++] = (byte)(x * 255 / widthSpan);
                raster[offset++] = (byte)(y * 255 / heightSpan);
                raster[offset++] = (byte)((x + y) * 255 / diagonalSpan);
            }
        }

        return raster;
    }

    /// <summary>
    /// A zlib stream carrying <paramref name="raw"/> in stored deflate blocks: the two-byte header, the
    /// blocks, and Adler-32 of the uncompressed data.
    /// </summary>
    private static byte[] Deflate(byte[] raw)
    {
        using MemoryStream stream = new();

        // CM = 8 (deflate), CINFO = 7 (32 KiB window); the second byte carries no preset dictionary and
        // makes the pair a multiple of 31, which is what a decoder checks.
        stream.WriteByte(0x78);
        stream.WriteByte(0x01);

        // DECLARED HERE RATHER THAN INSIDE THE LOOP, AND THAT IS CA2014 (F-108). A `stackalloc` is
        // released when the METHOD returns, not when the iteration ends, so one inside a loop grows the
        // frame once per pass — a temporary leak on any input and a stack overflow on a large enough
        // one. This loop runs once per 64 KiB of raster, so the 640px fixture ran it nineteen times for
        // seventy-six bytes: harmless in fact, and an error under `-p:ContinuousIntegrationBuild=true`,
        // which is what CI builds with. Hoisting is behaviour-identical because every pass overwrites
        // all four bytes before reading any of them.
        Span<byte> lengths = stackalloc byte[4];

        int offset = 0;
        while (true)
        {
            int take = Math.Min(StoredBlockLimit, raw.Length - offset);
            bool final = offset + take >= raw.Length;

            // BFINAL in bit 0 and BTYPE = 00 (stored) in bits 1-2. A stored block is byte-aligned from
            // here, which is the whole reason this encoding needs no bit writer.
            stream.WriteByte((byte)(final ? 1 : 0));

            BinaryPrimitives.WriteUInt16LittleEndian(lengths[..2], (ushort)take);
            BinaryPrimitives.WriteUInt16LittleEndian(lengths[2..], (ushort)~(ushort)take);
            stream.Write(lengths);

            stream.Write(raw, offset, take);
            offset += take;

            if (final)
            {
                break;
            }
        }

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));
        stream.Write(adler);

        return stream.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);

        png.Write(kind);
        png.Write(data);

        // The CRC covers the chunk type and the data, and not the length — which is the one detail of
        // this format that is easy to get wrong and produces a file every decoder rejects.
        byte[] covered = new byte[kind.Length + data.Length];
        kind.CopyTo(covered);
        data.CopyTo(covered.AsSpan(kind.Length));

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(covered));
        png.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(ReadOnlySpan<byte> bytes)
    {
        uint a = 1;
        uint b = 0;

        foreach (byte value in bytes)
        {
            a = (a + value) % AdlerModulus;
            b = (b + a) % AdlerModulus;
        }

        return (b << 16) | a;
    }
}
