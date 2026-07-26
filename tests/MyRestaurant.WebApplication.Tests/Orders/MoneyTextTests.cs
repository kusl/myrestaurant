using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Unit tests for <see cref="MoneyText"/> (TECHNICAL_SPECIFICATION §8.1, §13 — the currency code is
/// "display only"). Small, but worth pinning: the whole reason this type exists rather than
/// <c>ToString("C")</c> is that the framework's currency formatting reads the <em>server's</em> culture
/// and ignores <c>RESTAURANT_CURRENCY_CODE</c> entirely, which would print dollars for a restaurant
/// configured in euros and do it silently.
/// </summary>
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
        // numeric(10,2) throughout (§8.1), and a guest checking a bill against a menu board wants the
        // digits to line up rather than to be re-separated by whichever locale the container carries.
        Assert.Equal(expected, MoneyText.Format(amount, "USD"));
    }

    [Fact]
    public void ANegativeAmount_KeepsItsSignInFrontOfTheSymbol()
        => Assert.Equal("-$2.00", MoneyText.Format(-2.00m, "USD"));

    [Fact]
    public void AnEmptyCode_DegradesToBareDigitsRatherThanThrowing()
    {
        // RestaurantOptions validates the code at startup (§13), so this cannot happen in a running
        // application — but a formatter that throws on a screen is worse than one that renders plainly.
        Assert.Equal(" 4.50", MoneyText.Format(4.50m, string.Empty));
    }
}
