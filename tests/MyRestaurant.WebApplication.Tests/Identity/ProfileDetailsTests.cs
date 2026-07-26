using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the normalization and validation behind the profile page (TECHNICAL_SPECIFICATION §4.6,
/// §11.6). Pure — no container engine, no HTTP, no database — which is the whole reason this logic
/// was lifted out of the Razor component.
/// </summary>
public sealed class ProfileDetailsTests
{
    [Theory]
    // The cast matters: a bare [InlineData(null)] on a single-parameter theory is passed as the
    // argument *array*, not as one null argument, and xUnit throws at discovery.
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
        // A stray NUL or bell in a pasted name must vanish, not become a space: "Ad\0am" is "Adam".
        ProfileDetails details = ProfileDetails.Normalize("Ad\0am\u0007", null, null);

        Assert.Equal("Adam", details.DisplayName);
    }

    [Fact]
    public void Normalize_KeepsNonAsciiNames()
    {
        // Nothing here is ASCII-only: the display name is a human name.
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
    [InlineData("adam")]                 // no @
    [InlineData("@example.com")]         // nothing before the @
    [InlineData("adam@")]                // nothing after the @
    [InlineData("adam@@example.com")]    // two @
    [InlineData("adam@example")]         // undotted domain
    [InlineData("adam@.example.com")]    // domain begins with a dot
    [InlineData("adam@example.com.")]    // domain ends with a dot
    [InlineData("adam@-example.com")]    // domain begins with a hyphen
    [InlineData("adam baker@example.com")] // the space survives normalization inside the local part
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
    [InlineData("call the counter")] // letters
    [InlineData("+")]                // no digits at all
    [InlineData("()")]               // still no digits
    [InlineData("12")]               // fewer than three digits
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

        // A row written before this page existed may carry untrimmed text; that is not a change.
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

        // Recasing your own name is a real edit and must reach the database.
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
