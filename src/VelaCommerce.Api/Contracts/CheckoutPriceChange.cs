namespace VelaCommerce.Api.Contracts;

/// <summary>
/// One line whose price moved between the cart and the checkout, reported in the
/// <c>priceChanges</c> extension of the 409 that refuses the checkout.
/// <para>
/// The whole reason this type exists is that the alternative behaviours are both dishonest.
/// Charging the new price silently changes the total between the page the shopper read and the
/// card they were charged. Charging the old price sells at a number that may be weeks stale. So
/// the checkout fails, names every line that moved and by how much, and asks the shopper to look
/// again — which is the only version where nobody is surprised by their statement.
/// </para>
/// <para>
/// A withdrawn variant is reported here too, with <see cref="Now"/> null. It is the same class of
/// problem from the shopper's point of view — the cart no longer describes a purchasable thing —
/// and giving it a second status code would only mean two error screens where one will do.
/// </para>
/// </summary>
/// <param name="VariantId">The line's variant.</param>
/// <param name="Sku">SKU as captured on the cart line.</param>
/// <param name="DisplayName">Name as captured on the cart line.</param>
/// <param name="Was">The price the line was added at, and the one the shopper has been shown.</param>
/// <param name="Now">The catalog's live price, or null when the variant is no longer sold.</param>
/// <param name="Difference">
/// <paramref name="Now"/> minus <paramref name="Was"/>, signed, or null when there is no live
/// price to subtract. Positive means the shopper was holding a bargain that has expired.
/// </param>
public sealed record CheckoutPriceChange(
    Guid VariantId,
    string Sku,
    string DisplayName,
    MoneyDto Was,
    MoneyDto? Now,
    MoneyDto? Difference)
{
    /// <summary>
    /// True when the variant has left the catalog rather than merely changed price. Surfaced as a
    /// flag so a client can word the two cases differently without comparing nulls.
    /// </summary>
    public bool NoLongerSold => Now is null;
}

/// <summary>
/// The line that lost the race for the last unit, reported in the <c>shortfall</c> extension of
/// the 409 that refuses the checkout.
/// <para>
/// Exactly one line is named, not every line that is short: the reservation loop stops at the
/// first refusal and rolls back, so the others were never attempted and reporting them would mean
/// guessing. Naming the variant is what lets the storefront highlight the offending row instead of
/// showing a general apology.
/// </para>
/// </summary>
/// <param name="VariantId">The variant that could not be reserved.</param>
/// <param name="Sku">SKU as captured on the cart line.</param>
/// <param name="DisplayName">Name as captured on the cart line.</param>
/// <param name="Requested">Units the cart asked for.</param>
/// <param name="Available">
/// Units that were free when the shortfall was discovered, or null when the variant has no stock
/// record at all. Advisory only, and deliberately labelled as such: it is read after the failed
/// reservation, so another shopper may have taken more by the time it is displayed. The number
/// that decided the outcome was the one inside the conditional UPDATE, which is never read into
/// this process.
/// </param>
public sealed record CheckoutStockShortfall(
    Guid VariantId,
    string Sku,
    string DisplayName,
    int Requested,
    int? Available);
