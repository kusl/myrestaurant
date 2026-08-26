using System.Buffers.Binary;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal static class PictureFixtures
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int StoredBlockLimit = 0xFFFF;

    private const int AdlerModulus = 65521;

    internal static byte[] SquareGradientPng(int edge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(edge, 1);

        byte[] raster = Raster(edge, edge);

        using MemoryStream png = new();
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], edge);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], edge);
        header[8] = 8;
        header[9] = 2;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;

        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, Deflate(raster));
        WriteChunk(png, "IEND"u8, []);

        return png.ToArray();
    }

    private static byte[] Raster(int width, int height)
    {
        byte[] raster = new byte[height * (1 + (width * 3))];
        int offset = 0;

        int widthSpan = Math.Max(1, width - 1);
        int heightSpan = Math.Max(1, height - 1);
        int diagonalSpan = Math.Max(1, width + height - 2);

        for (int y = 0; y < height; y++)
        {
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

    private static byte[] Deflate(byte[] raw)
    {
        using MemoryStream stream = new();

        stream.WriteByte(0x78);
        stream.WriteByte(0x01);

        Span<byte> lengths = stackalloc byte[4];

        int offset = 0;
        while (true)
        {
            int take = Math.Min(StoredBlockLimit, raw.Length - offset);
            bool final = offset + take >= raw.Length;

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
