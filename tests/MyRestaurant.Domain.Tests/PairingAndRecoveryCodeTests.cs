using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Verifies the human-facing code formats (TECHNICAL_SPECIFICATION §4.2, §3.4): the unambiguous
/// 31-symbol alphabet (no I/L/O/0/1), the 8-character pairing code, and the ten single-use,
/// distinct recovery codes rendered as two dash-separated groups.
/// </summary>
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
    [InlineData("ABCDEFG")]        // too short
    [InlineData("ABCDEFGHJ")]      // too long
    [InlineData("ABCDEFG0")]       // contains an excluded digit
    [InlineData("ABCDEFGI")]       // contains an excluded letter
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
