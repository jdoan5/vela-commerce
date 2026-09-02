using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Catalog;

/// <summary>
/// A buyable SKU. Price changes are guarded: a cart holding a stale price is repriced
/// at checkout rather than honoured, and the difference is surfaced to the shopper.
/// </summary>
public sealed class ProductVariant : Entity
{
    private ProductVariant() { } // EF

    internal ProductVariant(Guid productId, string sku, string name, Money price, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new DomainException("SKU is required.");
        if (price.IsNegative) throw new DomainException("Price cannot be negative.");

        ProductId = productId;
        Sku = sku.Trim().ToUpperInvariant();
        Name = name?.Trim() ?? string.Empty;
        Price = price;
        ImageUrl = imageUrl;
    }

    public Guid ProductId { get; private set; }
    public Product? Product { get; private set; }

    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = string.Empty;
    public Money Price { get; private set; }
    public string? ImageUrl { get; private set; }

    public void Reprice(Money newPrice)
    {
        if (newPrice.IsNegative) throw new DomainException("Price cannot be negative.");
        if (!string.Equals(newPrice.Currency, Price.Currency, StringComparison.Ordinal))
            throw new DomainException("A variant cannot change currency.");
        Price = newPrice;
    }
}
