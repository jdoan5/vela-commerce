using VelaCommerce.Domain.Common;

namespace VelaCommerce.Api.Contracts;

/// <summary>
/// The current visitor's cart, and the only shape any cart endpoint returns — read or write.
/// <para>
/// Every mutation answers with the whole cart rather than with the line it touched, so a client
/// never has to reconstruct the subtotal locally and never has to follow a write with a read to
/// find out what the server thinks it now holds. The cart is small by construction (the domain
/// caps a line at 99 units) so the extra bytes are cheaper than the extra round trip and much
/// cheaper than two divergent copies of the total.
/// </para>
/// <para>
/// <strong>There is deliberately no cart id on the wire.</strong> The cart is identified by the
/// signed demo-session cookie and by nothing else. Publishing a cart's key would invite a client
/// to send it back, and the moment an API accepts a cart id it has to decide whether to trust it
/// — which is precisely the decision this phase exists to avoid ever having to make.
/// </para>
/// </summary>
/// <param name="Currency">
/// The cart's currency, fixed by the first item added. The domain refuses to mix currencies in
/// one cart, so a single code describes every amount below.
/// </param>
/// <param name="Lines">Lines in the order they were first added; re-adding a variant merges into its existing line rather than appending a new one.</param>
public sealed record CartResponse(string Currency, IReadOnlyList<CartLineResponse> Lines)
{
    /// <summary>
    /// The cart a visitor has before they have done anything — returned by GET when no cart row
    /// exists, which is the normal state for somebody who is only browsing. It exists so that
    /// "no cart yet" is an ordinary 200 with zero lines rather than a 404 the storefront has to
    /// write a special case for, and so that reading a cart never creates one.
    /// </summary>
    public static CartResponse Empty(string currency = Money.DefaultCurrency) => new(currency, []);

    /// <summary>
    /// Sum of the line totals at their captured prices — deliberately not at live prices. This is
    /// what the shopper agreed to as they built the cart; where the catalog has since moved, the
    /// per-line <see cref="CartLineResponse.PriceChanged"/> says so and checkout revalidates.
    /// Summing amounts and stamping the cart's currency on the result is safe only because the
    /// domain refuses to admit a second currency to a cart.
    /// </summary>
    public MoneyDto Subtotal => new(Lines.Sum(line => line.LineTotal.Amount), Currency);

    /// <summary>Units across all lines — the number a cart badge shows.</summary>
    public int TotalQuantity => Lines.Sum(line => line.Quantity);

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// True when at least one line's price has moved since it was added. Hoisted to the cart so a
    /// storefront can decide whether to draw a "prices have changed" banner without scanning the
    /// lines, and so the fact is impossible to miss in the response.
    /// </summary>
    public bool HasPriceChanges => Lines.Any(line => line.PriceChanged);

    /// <summary>
    /// True when a line refers to a variant the catalog no longer sells. Surfaced next to
    /// <see cref="HasPriceChanges"/> because both are reasons a checkout will not go through
    /// unchanged, and a shopper deserves to learn that on the cart page rather than at payment.
    /// </summary>
    public bool HasUnavailableLines => Lines.Any(line => !line.StillInCatalog);
}
