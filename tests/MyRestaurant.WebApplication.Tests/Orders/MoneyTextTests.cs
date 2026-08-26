using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class MoneyTextTests
{
    [Theory]
    [InlineData("USD", "$4.50")]
    [InlineData("EUR", "€4.50")]
    [InlineData("GBP", "£4.50")]
    [InlineData("JPY", "¥4.50")]
    [InlineData("INR", "₹4.50")]
    public void AKnownCode_IsRenderedWithItsSymbol(string currencyCode, string expected)
        => Assert.Equal(expected, MoneyText.Format(4.50m, currencyCode));

    [Fact]
    public void AnUnknownCode_FallsBackToTheCodeItself_RatherThanToAWrongSymbol()
        => Assert.Equal("ISK 1200.00", MoneyText.Format(1200m, "ISK"));

    [Fact]
    public void TheCodeIsMatchedWithoutRegardToCase()
        => Assert.Equal("$4.50", MoneyText.Format(4.50m, "usd"));

    [Fact]
    public void AnUnknownLowerCaseCode_IsUpperCasedInTheFallback()
        => Assert.Equal("ISK 4.50", MoneyText.Format(4.50m, "isk"));

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(4, "$4.00")]
    [InlineData(1200, "$1200.00")]
    public void TheAmountAlwaysCarriesTwoDecimalsAndIsNeverGrouped(int amount, string expected)
    {
        Assert.Equal(expected, MoneyText.Format(amount, "USD"));
    }

    [Fact]
    public void ANegativeAmount_KeepsItsSignInFrontOfTheSymbol()
        => Assert.Equal("-$2.00", MoneyText.Format(-2.00m, "USD"));

    [Fact]
    public void AnEmptyCode_DegradesToBareDigitsRatherThanThrowing()
    {
        Assert.Equal(" 4.50", MoneyText.Format(4.50m, string.Empty));
    }
}
