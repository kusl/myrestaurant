namespace MyRestaurant.Domain.Identifiers;

/// <summary>
/// UUIDv7 factory (ADR-0011) whose values ascend in the order they are minted, including inside one
/// millisecond, by carrying a dedicated counter in the <c>rand_a</c> field — RFC 9562 §6.2's first named
/// method for exactly this.
///
/// <para><b>Why the counter is here at all (F-95).</b> <c>Guid.CreateVersion7()</c> is
/// <c>Guid.NewGuid()</c> with the 48-bit Unix-millisecond timestamp written over the top and the version
/// and variant nibbles set; the other 74 bits stay cryptographically random. So it is time-ordered
/// <em>between</em> milliseconds and unordered <em>within</em> one, and two values minted in the same
/// millisecond sort at random — measured at 49.8% inverted, which is what a coin flip looks like. That was
/// not a theoretical concern: nine reads in <c>MyRestaurant.DataAccess</c> order by an instant and break
/// the tie on one of these identifiers, and every one of them was breaking the tie arbitrarily, because
/// every mutation in §8 stamps all the rows of its transaction with the same <c>IClock.UtcNow</c>.
/// <see cref="IIdentifierFactory.Create"/> records what depended on it.</para>
///
/// <para><b>The state is static, and that is deliberate rather than convenient.</b> The web application
/// registers this as a singleton, so an instance field would do there — but the guarantee is a property of
/// the <em>process's</em> stream of identifiers, not of one object's, and two instances handing out
/// interleaved values that each ascend on their own would satisfy an instance field while breaking the
/// contract. Making it static means the contract cannot be broken by a registration lifetime, by a test
/// that constructs its own factory, or by a second factory somebody adds later.</para>
///
/// <para><b>The packed value is the sort key, which is what makes this a compare-and-swap rather than a
/// lock.</b> <c>(milliseconds &lt;&lt; 12) | counter</c> is exactly the first 60 bits of the identifier in
/// the order PostgreSQL compares them, so "the next identifier ascends" and "the next packed value is
/// larger" are the same statement, and the whole of the concurrency argument is that this one
/// <see cref="long"/> only ever increases. 48 + 12 = 60 bits, so it cannot overflow a
/// <see cref="long"/>.</para>
///
/// <para><b>What happens at the two edges.</b> More than 4096 identifiers inside one millisecond exhausts
/// the counter, and rather than wrap — which would hand out a value that sorts <em>before</em> its
/// predecessor — the increment carries into the timestamp, so the identifier claims the next millisecond
/// and ordering holds. The identifier's embedded instant can therefore run briefly ahead of the wall
/// clock; nothing reads it, because every row that records a time records it in a <c>timestamptz</c>
/// column from <c>IClock</c>, and ADR-0011 already says these identifiers are not the time of record. A
/// clock that steps <em>backwards</em> is the same case seen from the other side and gets the same answer:
/// the candidate is behind the last value issued, so the last value issued plus one is used and the stream
/// does not double back.</para>
///
/// <para>The random bits are still the BCL's. <see cref="Guid.CreateVersion7()"/> is called for its 62
/// <c>rand_b</c> bits, which come from the same cryptographic source <see cref="Guid.NewGuid()"/> uses,
/// and for the version and variant nibbles it has already placed correctly. Only the timestamp and
/// <c>rand_a</c> are replaced below, so this type owns the ordering and owns nothing else.</para>
/// </summary>
public sealed class UuidV7IdentifierFactory : IIdentifierFactory
{
    /// <summary>RFC 9562 §5.7's <c>rand_a</c> is 12 bits, which is the counter's whole width.</summary>
    private const int CounterBits = 12;

    private const long CounterMask = (1L << CounterBits) - 1L;

    /// <summary>The <c>unix_ts_ms</c> field is 48 bits — good until the year 10,895.</summary>
    private const long TimestampMask = (1L << 48) - 1L;

    private const int UuidByteCount = 16;

    /// <summary>The version nibble, in the high half of byte 6.</summary>
    private const int Version7HighNibble = 0x70;

    /// <summary>
    /// <c>(milliseconds &lt;&lt; 12) | counter</c> for the most recent identifier issued by this process,
    /// or -1 before the first. Read and advanced only through <see cref="NextSortKey"/>.
    ///
    /// <para>-1 rather than 0 is not stylistic: 0 is a value the expression can legitimately produce (the
    /// Unix epoch, counter zero), and initialising a static field to its type's default is a thing an
    /// analyzer objects to. -1 is unreachable and means "nothing yet".</para>
    /// </summary>
    private static long _lastSortKey = -1L;

    public Guid Create()
    {
        long sortKey = NextSortKey();

        long milliseconds = (sortKey >> CounterBits) & TimestampMask;
        int counter = (int)(sortKey & CounterMask);

        // Seeded for its random bits and its variant, then overwritten where the ordering lives.
        Guid seeded = Guid.CreateVersion7();

        Span<byte> bytes = stackalloc byte[UuidByteCount];

        if (!seeded.TryWriteBytes(bytes, bigEndian: true, out int written) || written != UuidByteCount)
        {
            throw new InvalidOperationException(
                "A Guid did not write sixteen big-endian bytes into a sixteen-byte span, which the BCL"
                    + " guarantees it does. Nothing this type can do about that is better than saying so.");
        }

        // RFC 9562 §5.7, big-endian: bytes 0-5 are unix_ts_ms, byte 6 is the version nibble over the
        // high 4 bits of rand_a, byte 7 is the low 8 bits of rand_a. Byte 8 keeps the variant the
        // seed already set, and bytes 8-15 keep its randomness.
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

    /// <summary>
    /// Claims the next sort key, which is strictly larger than every key this process has already issued.
    ///
    /// <para>The loop is the ordinary compare-and-swap shape and it terminates for the ordinary reason: a
    /// failed exchange returns the value that beat it, so the next attempt is made against a key that has
    /// already advanced. A thread can only lose the race to a thread that made progress.</para>
    /// </summary>
    private static long NextSortKey()
    {
        long observed = Volatile.Read(ref _lastSortKey);

        while (true)
        {
            long candidate = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & TimestampMask) << CounterBits;

            // Behind or level with what has been issued — because the millisecond has not advanced, or
            // because the clock moved backwards. Either way the answer is the next key, which spends a
            // counter and carries into the timestamp when the counter is spent.
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
