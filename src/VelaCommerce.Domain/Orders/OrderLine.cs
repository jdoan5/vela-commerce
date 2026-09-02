using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Orders;

/// <summary>
/// A line copied from the cart at checkout. Name and SKU are denormalised on purpose:
/// an order must still read correctly after the catalog is renamed or the variant deleted.
/// </summary>
public sealed class OrderLine : Entity
{
    private OrderLine() { } // EF

    internal OrderLine(Guid orderId, Guid variantId, string sku, string displayName, Money unitPrice, int quantity)
    {
        if (quantity <= 0) throw new DomainException("Order line quantity must be positive.");
        if (unitPrice.IsNegative) throw new DomainException("Order line price cannot be negative.");

        OrderId = orderId;
        VariantId = variantId;
        Sku = sku;
        DisplayName = displayName;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    public Guid OrderId { get; private set; }
    public Guid VariantId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public Money UnitPrice { get; private set; }
    public int Quantity { get; private set; }

    public Money LineTotal => UnitPrice * Quantity;
}
