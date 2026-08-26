using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class PairingAndRecoveryCodeTests
{
    [Fact]
    public void Alphabet_ExcludesAmbiguousCharacters()
    {
        Assert.Equal(31, PairingCode.Alphabet.Length);
        foreach (char ambiguous in "ILO01")
        {
            Assert.DoesNotContain(ambiguous, PairingCode.Alphabet);
        }
    }

    [Fact]
    public void PairingCode_IsEightCharactersFromTheAlphabet()
    {
        string code = PairingCode.Generate();

        Assert.Equal(PairingCode.Length, code.Length);
        Assert.True(PairingCode.IsWellFormed(code));
        Assert.All(code, character => Assert.Contains(character, PairingCode.Alphabet));
    }

    [Theory]
    [InlineData("ABCDEFG")]
    [InlineData("ABCDEFGHJ")]
    [InlineData("ABCDEFG0")]
    [InlineData("ABCDEFGI")]
    public void IsWellFormed_RejectsBadCodes(string code)
        => Assert.False(PairingCode.IsWellFormed(code));

    [Fact]
    public void RecoveryCode_HasTwoDashSeparatedGroups()
    {
        string code = RecoveryCode.GenerateOne();

        string[] groups = code.Split('-');
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Equal(RecoveryCode.GroupLength, group.Length));
        Assert.All(
            groups,
            group => Assert.All(group, character => Assert.Contains(character, PairingCode.Alphabet)));
    }

    [Fact]
    public void GenerateSet_ReturnsTenDistinctCodes()
    {
        IReadOnlyList<string> codes = RecoveryCode.GenerateSet();

        Assert.Equal(RecoveryCode.CodesPerSet, codes.Count);
        Assert.Equal(RecoveryCode.CodesPerSet, codes.Distinct().Count());
    }
}
