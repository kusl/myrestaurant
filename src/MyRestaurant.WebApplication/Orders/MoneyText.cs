using System.Globalization;

namespace MyRestaurant.WebApplication.Orders;

public static class MoneyText
{
    private static readonly Dictionary<string, string> SymbolsByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUD"] = "$",
        ["CAD"] = "$",
        ["CNY"] = "¥",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["INR"] = "₹",
        ["JPY"] = "¥",
        ["KRW"] = "₩",
        ["MXN"] = "$",
        ["NPR"] = "रू",
        ["NZD"] = "$",
        ["PHP"] = "₱",
        ["SGD"] = "$",
        ["USD"] = "$",
        ["ZAR"] = "R",
    };

    public static string Format(decimal amount, string? currencyCode)
    {
        string marker = SymbolsByCode.TryGetValue(currencyCode ?? string.Empty, out string? symbol)
            ? symbol
            : $"{(currencyCode ?? string.Empty).ToUpperInvariant()} ";

        string sign = amount < 0m ? "-" : string.Empty;
        string digits = Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture);

        return string.Concat(sign, marker, digits);
    }
}
