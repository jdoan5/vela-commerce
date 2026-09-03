using System.Globalization;

namespace VelaCommerce.Storefront.Catalog;

/// <summary>
/// The single place a price becomes a string.
/// <para>
/// Every amount in this application is a count of minor units. Converting one to text is
/// where a rounding bug would be introduced, so it happens here, once, with
/// <see cref="decimal"/> arithmetic — never <see cref="double"/> — and against the
/// invariant culture so the same snapshot renders the same price on every machine.
/// </para>
/// </summary>
public static class MoneyFormatter
{
    /// <summary>
    /// Currencies this catalog can actually contain. The seed is USD-only; the map exists
    /// so a second currency renders correctly rather than falling back to a bare code, and
    /// so nobody is tempted to hardcode a dollar sign in a component.
    /// </summary>
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["CAD"] = "$",
        ["AUD"] = "$",
        ["NZD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["JPY"] = "¥",
        ["SEK"] = "kr",
        ["NOK"] = "kr",
        ["DKK"] = "kr",
    };

    /// <summary>
    /// Minor units per major unit. Only the exceptions to "two decimals" are listed; the
    /// seed's USD is not one of them, but a zero-decimal currency formatted as if it had
    /// cents would be wrong by a factor of a hundred, which is worth the four lines.
    /// </summary>
    private static readonly Dictionary<string, int> Exponents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPY"] = 0,
        ["KRW"] = 0,
        ["VND"] = 0,
        ["ISK"] = 0,
        ["CLP"] = 0,
        ["BHD"] = 3,
        ["KWD"] = 3,
        ["OMR"] = 3,
        ["TND"] = 3,
    };

    /// <summary>The currency's display symbol, or its ISO code when there is no symbol to show.</summary>
    public static string Symbol(string currency) =>
        Symbols.TryGetValue(currency, out var symbol) ? symbol : currency.ToUpperInvariant();

    /// <summary>
    /// The amount alone, grouped and with the right number of decimals — "1,299.00" — with
    /// no symbol. Components that want to typeset the symbol separately use this.
    /// </summary>
    public static string Amount(long amountMinorUnits, string currency)
    {
        var exponent = Exponents.TryGetValue(currency, out var e) ? e : 2;
        var scale = exponent switch { 0 => 1m, 3 => 1000m, _ => 100m };
        var major = amountMinorUnits / scale;
        return major.ToString("N" + exponent.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>The full display string, symbol and all — "$1,299.00".</summary>
    public static string Format(long amountMinorUnits, string currency) =>
        Symbol(currency) + Amount(amountMinorUnits, currency);

    /// <summary>Convenience overload for a snapshot amount; returns an em dash when there is no price.</summary>
    public static string Format(CatalogMoney? money) =>
        money is null ? "—" : Format(money.AmountMinorUnits, money.Currency);
}
