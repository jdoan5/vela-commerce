using System.Globalization;

using VelaCommerce.Domain.Common;

namespace VelaCommerce.Api.Contracts;

/// <summary>
/// Money as it crosses the wire.
/// <para>
/// <see cref="Amount"/> is minor units (cents) because a JSON number that reaches a
/// JavaScript client as a <c>double</c> is exactly how "$19.99" becomes 19.989999999999998.
/// <see cref="Display"/> ships alongside it so the storefront, the admin UI and the OpenAPI
/// examples all render the same string instead of each re-deriving currency formatting from
/// a locale the server never told them about.
/// </para>
/// <para>
/// The record deliberately takes only the two persisted columns in its constructor: that is the
/// shape EF Core can build directly inside a projection — including a nested one — so no query
/// in this API has to materialise a <see cref="Money"/> just to reformat it.
/// </para>
/// </summary>
public sealed record MoneyDto(long Amount, string Currency)
{
    /// <summary>Presentation string, computed on materialisation rather than in SQL.</summary>
    public string Display => Format(Amount, Currency);

    /// <summary>
    /// Builds a value from an aggregate that legitimately has nothing to aggregate: a product
    /// with no live variants has no "from" price, and the honest answer there is null, not zero.
    /// Only ever called from a top-level projection, where EF permits client evaluation.
    /// </summary>
    public static MoneyDto? Optional(long? amount, string? currency) =>
        amount is null ? null : new MoneyDto(amount.Value, currency ?? Money.DefaultCurrency);

    private static string Format(long amount, string currency)
    {
        var code = string.IsNullOrWhiteSpace(currency)
            ? Money.DefaultCurrency
            : currency.ToUpperInvariant();

        // Invariant culture keeps the payload byte-identical whatever the container's locale is;
        // the sign is pulled out front so a refund reads "-$5.00" and not "$-5.00".
        var magnitude = (Math.Abs(amount) / 100m).ToString("N2", CultureInfo.InvariantCulture);
        var sign = amount < 0 ? "-" : string.Empty;

        // Symbols only for currencies the seeded catalog actually uses. Anything else falls back
        // to the ISO code, which is less pretty but never ambiguous between $ and $.
        return code switch
        {
            "USD" => $"{sign}${magnitude}",
            "EUR" => $"{sign}€{magnitude}",
            "GBP" => $"{sign}£{magnitude}",
            _ => $"{sign}{magnitude} {code}",
        };
    }
}
