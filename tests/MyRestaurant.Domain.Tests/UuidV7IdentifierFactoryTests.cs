using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Confirms the identifier factory produces version-7 UUIDs (ADR-0011) and does not repeat, so
/// primary keys are application-generated and naturally time-ordered.
/// </summary>
public sealed class UuidV7IdentifierFactoryTests
{
    private readonly IIdentifierFactory _factory = new UuidV7IdentifierFactory();

    [Fact]
    public void Create_ProducesVersion7Uuids()
        => Assert.Equal(7, _factory.Create().Version);

    [Fact]
    public void Create_DoesNotRepeat()
    {
        HashSet<Guid> identifiers = [];
        for (int index = 0; index < 1000; index++)
        {
            Assert.True(identifiers.Add(_factory.Create()));
        }
    }
}
