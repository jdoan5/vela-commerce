using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Catalog;

/// <summary>
/// A catalog product. The aggregate root for its variants: variants are only ever
/// added or priced through here, so a product can never hold two variants with the
/// same SKU.
/// </summary>
public sealed class Product : Entity
{
    private readonly List<ProductVariant> _variants = [];

    private Product() { } // EF

    public Product(string slug, string name, string description, string category)
    {
        if (string.IsNullOrWhiteSpace(slug)) throw new DomainException("Product slug is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Product name is required.");

        Slug = slug.Trim().ToLowerInvariant();
        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        Category = category?.Trim() ?? "uncategorized";
    }

    public string Slug { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public string Category { get; private set; } = null!;

    /// <summary>Free-form facets (colour, material, dimensions). Stored as jsonb.</summary>
    public Dictionary<string, string> Attributes { get; private set; } = [];

    public IReadOnlyList<ProductVariant> Variants => _variants;

    public ProductVariant AddVariant(string sku, string variantName, Money price, string? imageUrl = null)
    {
        if (_variants.Any(v => string.Equals(v.Sku, sku, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"SKU '{sku}' already exists on product '{Slug}'.");

        var variant = new ProductVariant(Id, sku, variantName, price, imageUrl);
        _variants.Add(variant);
        return variant;
    }

    /// <summary>Lowest variant price, used for the "from $X" catalog card.</summary>
    public Money? FromPrice => _variants.Count == 0
        ? null
        : _variants.Where(v => !v.IsDeleted).Select(v => v.Price).DefaultIfEmpty().Min();
}
