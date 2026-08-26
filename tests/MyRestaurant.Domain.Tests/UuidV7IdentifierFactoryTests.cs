using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class UuidV7IdentifierFactoryTests
{
    private readonly IIdentifierFactory _factory = new UuidV7IdentifierFactory();

    [Fact]
    public void Create_ProducesVersion7Uuids()
        => Assert.Equal(7, _factory.Create().Version);

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

    private static byte[] SortKeyOf(Guid identifier) => identifier.ToByteArray(bigEndian: true);

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
