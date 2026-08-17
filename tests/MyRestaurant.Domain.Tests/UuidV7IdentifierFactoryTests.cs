using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// The identifier factory produces version-7 UUIDs (ADR-0011), does not repeat, and — the reason most of
/// this file exists — hands out values that <b>ascend in the order they were minted</b>, which is the
/// contract <see cref="IIdentifierFactory.Create"/> now states and F-95 found nothing was keeping.
///
/// <para><b>Every comparison here is PostgreSQL's, spelled out as bytes, and that is the load-bearing
/// decision in the file.</b> The property under test only matters because nine reads in the data access
/// layer order by an instant and break the tie on one of these identifiers, so the relation that must hold
/// is the one <em>the database</em> applies: unsigned, byte-wise, over the sixteen bytes in RFC 9562
/// layout. <see cref="Guid.CompareTo(Guid)"/> is not that relation — it reads the second field as a signed
/// 16-bit integer, and that field holds the low sixteen bits of the millisecond, so the two orders
/// genuinely disagree whenever a pair straddles a boundary where those bits cross 0x7FFF. A version of this
/// file written with <c>CompareTo</c>, or with <c>Comparer&lt;Guid&gt;.Default</c>, or with
/// <c>OrderBy(identifier => identifier)</c>, would pass while asserting something the reads do not use.
/// <see cref="SortKeyOf"/> is here so that no assertion below can quietly acquire the wrong one.</para>
///
/// <para>No container and no clock injection: the factory reads the system clock and the property is about
/// the order of its own output, so these are unit tests that run in milliseconds. The state the ordering
/// rests on is process-wide by design, which is why <see cref="Create_AscendsAcrossTwoInstances"/> can ask
/// the question it asks — and why nothing here may assert a starting value.</para>
/// </summary>
public sealed class UuidV7IdentifierFactoryTests
{
    private readonly IIdentifierFactory _factory = new UuidV7IdentifierFactory();

    [Fact]
    public void Create_ProducesVersion7Uuids()
        => Assert.Equal(7, _factory.Create().Version);

    /// <summary>
    /// The RFC 9562 variant bits survive having the timestamp and counter written over the value. They are
    /// in byte 8, one byte past the last one the factory touches, so this is the assertion that says the
    /// factory stopped where it meant to.
    /// </summary>
    [Fact]
    public void Create_KeepsTheRfc9562Variant()
    {
        for (int index = 0; index < 100; index++)
        {
            byte variantByte = _factory.Create().ToByteArray(bigEndian: true)[8];

            Assert.Equal(0x80, variantByte & 0xC0);
        }
    }

    [Fact]
    public void Create_DoesNotRepeat()
    {
        HashSet<Guid> identifiers = [];
        for (int index = 0; index < 1000; index++)
        {
            Assert.True(identifiers.Add(_factory.Create()));
        }
    }

    /// <summary>
    /// <b>The assertion F-95 is about.</b> A tight loop mints far more than one millisecond's worth of
    /// identifiers, so most adjacent pairs share a millisecond — which is exactly the arrangement every
    /// write in §8 creates, because a transaction stamps all of its rows with one <c>IClock.UtcNow</c> and
    /// then mints an identifier per row.
    ///
    /// <para>On the tree before the fix this failed on roughly the first pair it looked at: the values were
    /// <c>Guid.CreateVersion7()</c>, whose 74 non-timestamp bits are random, so same-millisecond pairs were
    /// inverted about half the time and a run of a thousand had hundreds of inversions. A single pair would
    /// have been a coin flip and therefore a flaky test; a thousand is a certainty in both directions, and
    /// that is the whole reason for the count.</para>
    /// </summary>
    [Fact]
    public void Create_AscendsAcrossABurstInTheOrderPostgreSqlReads()
    {
        Guid[] minted = new Guid[1000];

        for (int index = 0; index < minted.Length; index++)
        {
            minted[index] = _factory.Create();
        }

        AssertAscending(minted);
    }

    /// <summary>
    /// Two factories in one process are one ascending stream. The web application registers a singleton, so
    /// nothing in production depends on this — but the guarantee is about the process, and an
    /// implementation that kept its counter per instance would satisfy every other assertion in this file
    /// while handing two callers values that interleave in the wrong order. This is the assertion that
    /// makes the state's being static a tested decision rather than an implementation detail.
    /// </summary>
    [Fact]
    public void Create_AscendsAcrossTwoInstances()
    {
        IIdentifierFactory first = new UuidV7IdentifierFactory();
        IIdentifierFactory second = new UuidV7IdentifierFactory();

        Guid[] minted = new Guid[400];

        for (int index = 0; index < minted.Length; index++)
        {
            minted[index] = index % 2 == 0 ? first.Create() : second.Create();
        }

        AssertAscending(minted);
    }

    /// <summary>
    /// More identifiers inside one millisecond than the 12-bit counter can express. The counter must carry
    /// into the timestamp rather than wrap, because a wrap hands out a value that sorts before its own
    /// predecessor — the failure this whole file exists to refuse, arriving from the one direction a
    /// correct-looking counter still permits.
    ///
    /// <para>Whether 5000 iterations actually land in one millisecond is not asserted and does not need to
    /// be: if they do, the counter is exhausted and the carry is exercised, and if the machine is slow
    /// enough that they do not, the ordering must hold anyway. Either way the assertion is the same one,
    /// which is what makes this test deterministic on hardware nobody has seen.</para>
    /// </summary>
    [Fact]
    public void Create_AscendsPastTheCountersCapacity()
    {
        Guid[] minted = new Guid[5000];

        for (int index = 0; index < minted.Length; index++)
        {
            minted[index] = _factory.Create();
        }

        AssertAscending(minted);
    }

    /// <summary>
    /// Under concurrency there is no minting order to assert, so this asserts the invariant that survives
    /// the absence of one: <b>no two identifiers share a sort key.</b> The first eight bytes are the
    /// timestamp and the counter, which is the whole of what the compare-and-swap hands out, so two
    /// identifiers with equal first eight bytes mean two threads were given the same key — and a duplicate
    /// key is a pair of rows whose order is decided by the random bits again, which is the defect back
    /// under a smaller name.
    ///
    /// <para>Distinctness of the sort keys is a strictly stronger claim than distinctness of the
    /// identifiers, which is why it is what gets asserted: the full values would differ by their random
    /// bits even if the counter had handed the same number out twice.</para>
    /// </summary>
    [Fact]
    public void Create_MintsDistinctSortKeysUnderConcurrency()
    {
        const int workers = 8;
        const int perWorker = 500;

        Guid[][] minted = new Guid[workers][];

        Parallel.For(0, workers, worker =>
        {
            Guid[] mine = new Guid[perWorker];

            for (int index = 0; index < perWorker; index++)
            {
                mine[index] = _factory.Create();
            }

            minted[worker] = mine;
        });

        List<string> sortKeys = minted
            .SelectMany(batch => batch)
            .Select(identifier => Convert.ToHexString(identifier.ToByteArray(bigEndian: true)[..8]))
            .ToList();

        Assert.Equal(workers * perWorker, sortKeys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The sixteen bytes in RFC 9562 order, which is the sequence PostgreSQL compares as unsigned bytes
    /// when it evaluates <c>ORDER BY … _identifier</c>. Named rather than inlined so that the reason for
    /// not using the obvious comparison is written once, next to the thing that replaces it.
    /// </summary>
    private static byte[] SortKeyOf(Guid identifier) => identifier.ToByteArray(bigEndian: true);

    /// <summary>
    /// Every adjacent pair ascends, reported with the index and both values so a failure names the pair
    /// rather than only the fact that one exists.
    /// </summary>
    private static void AssertAscending(Guid[] minted)
    {
        List<string> inversions = [];

        for (int index = 1; index < minted.Length; index++)
        {
            if (Compare(SortKeyOf(minted[index - 1]), SortKeyOf(minted[index])) >= 0)
            {
                inversions.Add($"[{index - 1}] {minted[index - 1]} is not before [{index}] {minted[index]}");
            }
        }

        Assert.True(
            inversions.Count == 0,
            $"{inversions.Count} of {minted.Length - 1} adjacent pairs do not ascend under PostgreSQL's"
                + " uuid ordering, so the identifier is not a tie-break for two rows that share an"
                + " occurred_at — which is what nine reads and OrderProjection use it as (F-95). First few:"
                + $" {string.Join("; ", inversions.Take(3))}.");
    }

    private static int Compare(byte[] left, byte[] right)
    {
        for (int index = 0; index < left.Length; index++)
        {
            int difference = left[index].CompareTo(right[index]);

            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }
}
