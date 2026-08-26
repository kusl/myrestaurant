using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class ProfileDetailsTests
{
    [Theory]

    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Normalize_TurnsBlankIntoNull(string? blank)
    {
        ProfileDetails details = ProfileDetails.Normalize(blank, blank, blank);

        Assert.Null(details.DisplayName);
        Assert.Null(details.EmailAddress);
        Assert.Null(details.PhoneNumber);
    }

    [Theory]
    [InlineData("  Adam  ", "Adam")]
    [InlineData("Adam   Baker", "Adam Baker")]
    [InlineData("\tBetty\nCarter ", "Betty Carter")]
    [InlineData("Booth  1", "Booth 1")]
    public void Normalize_TrimsAndCollapsesWhitespace(string input, string expected)
        => Assert.Equal(expected, ProfileDetails.Normalize(input, null, null).DisplayName);

    [Fact]
    public void Normalize_DropsControlCharactersWithoutIntroducingWordBreaks()
    {
        ProfileDetails details = ProfileDetails.Normalize("Ad\0am\u0007", null, null);

        Assert.Equal("Adam", details.DisplayName);
    }

    [Fact]
    public void Normalize_KeepsNonAsciiNames()
    {
        ProfileDetails details = ProfileDetails.Normalize("Zoë Ngô", null, null);

        Assert.Equal("Zoë Ngô", details.DisplayName);
    }

    [Fact]
    public void Validate_AcceptsAllThreeFieldsUnset()
        => Assert.Empty(ProfileDetails.Normalize(null, null, null).Validate());

    [Fact]
    public void Validate_AcceptsAPlausibleSet()
    {
        ProfileDetails details = ProfileDetails.Normalize("Adam Baker", "adam@example.com", "+1 (757) 555-0143");

        Assert.Empty(details.Validate());
    }

    [Theory]
    [InlineData("adam@example.com")]
    [InlineData("adam.baker+orders@mail.example.co.uk")]
    [InlineData("a@b.cd")]
    public void Validate_AcceptsEmailAddressesThatLookLikeAddresses(string emailAddress)
        => Assert.Empty(ProfileDetails.Normalize(null, emailAddress, null).Validate());

    [Theory]
    [InlineData("adam")]
    [InlineData("@example.com")]
    [InlineData("adam@")]
    [InlineData("adam@@example.com")]
    [InlineData("adam@example")]
    [InlineData("adam@.example.com")]
    [InlineData("adam@example.com.")]
    [InlineData("adam@-example.com")]
    [InlineData("adam baker@example.com")]
    public void Validate_RejectsEmailAddressesThatDoNot(string emailAddress)
        => Assert.Single(ProfileDetails.Normalize(null, emailAddress, null).Validate());

    [Theory]
    [InlineData("7575550143")]
    [InlineData("+1 757 555 0143")]
    [InlineData("(757) 555-0143")]
    [InlineData("757.555.0143")]
    [InlineData("020 7946 0018 #22")]
    public void Validate_AcceptsPhoneNumbersPeopleActuallyType(string phoneNumber)
        => Assert.Empty(ProfileDetails.Normalize(null, null, phoneNumber).Validate());

    [Theory]
    [InlineData("call the counter")]
    [InlineData("+")]
    [InlineData("()")]
    [InlineData("12")]
    public void Validate_RejectsPhoneNumbersThatAreNot(string phoneNumber)
        => Assert.Single(ProfileDetails.Normalize(null, null, phoneNumber).Validate());

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        ProfileDetails details = ProfileDetails.Normalize(
            new string('x', ProfileDetails.DisplayNameMaximumLength + 1),
            "not-an-address",
            "no digits here");

        Assert.Equal(3, details.Validate().Count);
    }

    [Fact]
    public void Validate_RejectsAnOverlongDisplayName_ButAcceptsOneAtTheLimit()
    {
        Assert.Empty(ProfileDetails.Normalize(new string('x', ProfileDetails.DisplayNameMaximumLength), null, null).Validate());
        Assert.Single(ProfileDetails.Normalize(new string('x', ProfileDetails.DisplayNameMaximumLength + 1), null, null).Validate());
    }

    [Fact]
    public void SameAs_NormalizesTheStoredSideToo()
    {
        ProfileDetails details = ProfileDetails.Normalize("Adam Baker", "adam@example.com", "7575550143");

        Assert.True(details.SameAs("  Adam  Baker ", "adam@example.com", "7575550143"));
    }

    [Fact]
    public void SameAs_IsCaseInsensitiveForEmail_BecauseTheColumnIsCitext()
    {
        ProfileDetails details = ProfileDetails.Normalize(null, "adam@example.com", null);

        Assert.True(details.SameAs(null, "Adam@Example.COM", null));
    }

    [Fact]
    public void SameAs_IsCaseSensitiveForDisplayName()
    {
        ProfileDetails details = ProfileDetails.Normalize("adam", null, null);

        Assert.False(details.SameAs("Adam", null, null));
    }

    [Theory]
    [InlineData("Adam", null, null, null, null, null)]
    [InlineData(null, null, null, "Adam", null, null)]
    [InlineData(null, "adam@example.com", null, null, null, null)]
    [InlineData(null, null, "7575550143", null, null, "7575550144")]
    public void SameAs_DetectsEverySingleFieldChange(
        string? displayName,
        string? emailAddress,
        string? phoneNumber,
        string? storedDisplayName,
        string? storedEmailAddress,
        string? storedPhoneNumber)
    {
        ProfileDetails details = ProfileDetails.Normalize(displayName, emailAddress, phoneNumber);

        Assert.False(details.SameAs(storedDisplayName, storedEmailAddress, storedPhoneNumber));
    }
}
