namespace VelaCommerce.Api.Contracts;

/// <summary>
/// One buyable SKU, with the availability figure the product page needs to decide between
/// "Add to cart", "Only 2 left" and a disabled button.
/// </summary>
/// <param name="Available">
/// On-hand minus reserved for this variant, or 0 when no stock row exists at all. It is a
/// read-time snapshot and explicitly not a promise: the reservation that actually decides
/// whether the shopper gets the unit is a conditional UPDATE at checkout, so this number is
/// allowed to be stale by the time the button is clicked.
/// </param>
public sealed record ProductVariantResponse(
    Guid Id,
    string Sku,
    string Name,
    MoneyDto Price,
    string? ImageUrl,
    int Available)
{
    /// <summary>Derived so the client never has to agree with the server on what "in stock" means.</summary>
    public bool InStock => Available > 0;
}
