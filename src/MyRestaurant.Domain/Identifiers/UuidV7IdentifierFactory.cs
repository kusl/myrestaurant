namespace MyRestaurant.Domain.Identifiers;

public sealed class UuidV7IdentifierFactory : IIdentifierFactory
{
    private const int CounterBits = 12;

    private const long CounterMask = (1L << CounterBits) - 1L;

    private const long TimestampMask = (1L << 48) - 1L;

    private const int UuidByteCount = 16;

    private const int Version7HighNibble = 0x70;

    private static long _lastSortKey = -1L;

    public Guid Create()
    {
        long sortKey = NextSortKey();

        long milliseconds = (sortKey >> CounterBits) & TimestampMask;
        int counter = (int)(sortKey & CounterMask);

        Guid seeded = Guid.CreateVersion7();

        Span<byte> bytes = stackalloc byte[UuidByteCount];

        if (!seeded.TryWriteBytes(bytes, bigEndian: true, out int written) || written != UuidByteCount)
        {
            throw new InvalidOperationException(
                "A Guid did not write sixteen big-endian bytes into a sixteen-byte span, which the BCL"
                    + " guarantees it does. Nothing this type can do about that is better than saying so.");
        }

        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;
        bytes[6] = (byte)(Version7HighNibble | ((counter >> 8) & 0x0F));
        bytes[7] = (byte)counter;

        return new Guid(bytes, bigEndian: true);
    }

    private static long NextSortKey()
    {
        long observed = Volatile.Read(ref _lastSortKey);

        while (true)
        {
            long candidate = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & TimestampMask) << CounterBits;

            if (candidate <= observed)
            {
                candidate = observed + 1L;
            }

            long actual = Interlocked.CompareExchange(ref _lastSortKey, candidate, observed);

            if (actual == observed)
            {
                return candidate;
            }

            observed = actual;
        }
    }
}
