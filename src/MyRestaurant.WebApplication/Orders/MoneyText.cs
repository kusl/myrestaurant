using System.Globalization;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// Renders a <c>numeric(10,2)</c> money amount for a screen (TECHNICAL_SPECIFICATION §8.1, §13:
/// "a single restaurant-wide currency code (<c>RESTAURANT_CURRENCY_CODE</c>, default <c>USD</c>) used
/// for display only").
///
/// <para><b>Why not <see cref="CultureInfo"/>'s currency formatting.</b> The obvious
/// <c>amount.ToString("C")</c> takes its symbol, its decimal separator, and its symbol placement from
/// the <em>server's</em> culture, which in this deployment is whatever locale the container image
/// happens to carry — and it would silently ignore <c>RESTAURANT_CURRENCY_CODE</c> entirely, printing
/// dollars for a restaurant configured in euros. Building a matching <see cref="RegionInfo"/> from an
/// ISO 4217 code is not possible in either direction without a lookup table, so the lookup table is
/// here, small and explicit.</para>
///
/// <para>The amount itself is always formatted invariantly with two decimals: prices are stored as
/// <c>numeric(10,2)</c> and a guest checking a bill against a menu board wants the digits to line up,
/// not to be grouped or re-separated by a locale nobody chose. An unknown code falls back to the code
/// and a space — <c>ISK 1200.00</c> — which is unambiguous, and adding a symbol is a one-line change
/// below.</para>
/// </summary>
public static class MoneyText
{
    /// <summary>
    /// ISO 4217 → the symbol to prefix. Deliberately short: the currencies a self-hosted single
    /// restaurant is most likely to be configured in. This is a display convenience, not a claim to
    /// completeness, and a missing entry degrades to the code rather than to a wrong symbol.
    /// </summary>
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

    /// <summary>
    /// The amount with its currency marker, e.g. <c>$12.50</c> or <c>ISK 1200.00</c>. A negative amount
    /// keeps its sign in front of the marker (<c>-$2.00</c>), which is how a correction reads.
    /// </summary>
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
