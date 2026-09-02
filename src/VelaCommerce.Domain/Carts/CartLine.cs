using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Carts;

public sealed class CartLine : Entity
{
    private CartLine() { } // EF

    /// <summary>
    /// The demo caps a line so one visitor cannot reserve the whole catalog.
    /// </summary>
    public const int MaxQuantity = 99;

    internal CartLine(Guid cartId, Guid variantId, string sku, string displayName, Money unitPrice, int quantity)
    {
        // Validated here rather than in Cart.AddItem so that both paths into a line —
        // creating one and changing an existing one — pass through the same guard.
        AssertQuantityInRange(quantity);

        CartId = cartId;
        VariantId = variantId;
        Sku = sku;
        DisplayName = displayName;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    private static void AssertQuantityInRange(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be positive; remove the line instead.");
        if (quantity > MaxQuantity) throw new DomainException($"Quantity is capped at {MaxQuantity} per line on the demo.");
    }

    public Guid CartId { get; private set; }
    public Guid VariantId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;

    /// <summary>Price when the line was added. Compared against live price at checkout.</summary>
    public Money UnitPrice { get; private set; }
    public int Quantity { get; private set; }

    public Money LineTotal => UnitPrice * Quantity;

    internal void ChangeQuantity(int quantity)
    {
        AssertQuantityInRange(quantity);
        Quantity = quantity;
    }

    /// <summary>Accepts a new live price when the catalog moved under the shopper.</summary>
    internal void Reprice(Money newPrice) => UnitPrice = newPrice;
}
