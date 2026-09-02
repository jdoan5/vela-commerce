namespace VelaCommerce.Api.Contracts;

/// <summary>
/// A single product with everything the product page draws, in one response.
/// <para>
/// Variants and their availability are embedded rather than exposed as a second endpoint
/// because the page cannot render without them, and a second request would mean a second
/// chance to pay this deployment's cold start.
/// </para>
/// </summary>
/// <param name="Attributes">
/// Free-form facets stored as jsonb (colour, material, dimensions). Passed through verbatim:
/// the API has no opinion on the keys, which is the point of storing them as a document.
/// </param>
/// <param name="Variants">Live variants, cheapest first, so the default selection is the entry price.</param>
public sealed record ProductDetailResponse(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string Category,
    IReadOnlyDictionary<string, string> Attributes,
    MoneyDto? FromPrice,
    IReadOnlyList<ProductVariantResponse> Variants);
