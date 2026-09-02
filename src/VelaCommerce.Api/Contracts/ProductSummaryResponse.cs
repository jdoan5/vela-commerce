namespace VelaCommerce.Api.Contracts;

/// <summary>
/// A product as it appears on a catalog card.
/// <para>
/// Deliberately not the same shape as <see cref="ProductDetailResponse"/>: the grid renders
/// hundreds of these, so it carries a variant count and a "from" price instead of the variant
/// rows themselves. Shipping every variant here would multiply the payload by roughly the
/// average variant count for data the card never draws.
/// </para>
/// </summary>
/// <param name="FromPrice">
/// Cheapest live variant price, or null when the product has no live variants — which is a
/// real state (everything discontinued) and not the same thing as free.
/// </param>
/// <param name="ImageUrl">First live variant's image, used as the card thumbnail.</param>
public sealed record ProductSummaryResponse(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string Category,
    int VariantCount,
    MoneyDto? FromPrice,
    string? ImageUrl);
