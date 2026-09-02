using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Inventory;

/// <summary>
/// Stock for one variant, expressed as on-hand minus reserved.
/// <para>
/// The race this exists to lose safely: two shoppers checking out the last unit at the
/// same moment. <see cref="TryReserve"/> encodes the rule, but the rule is *enforced* by
/// a conditional UPDATE in the database plus a CHECK constraint, because two processes
/// each holding a valid in-memory object would otherwise both succeed.
/// </para>
/// </summary>
public sealed class StockItem : Entity
{
    private StockItem() { } // EF

    public StockItem(Guid variantId, int onHand)
    {
        if (onHand < 0) throw new DomainException("On-hand quantity cannot be negative.");
        VariantId = variantId;
        OnHand = onHand;
    }

    public Guid VariantId { get; private set; }

    /// <summary>Physical units in the warehouse.</summary>
    public int OnHand { get; private set; }

    /// <summary>Units promised to carts/orders that have not yet shipped.</summary>
    public int Reserved { get; private set; }

    /// <summary>What a shopper may still take. Never negative.</summary>
    public int Available => OnHand - Reserved;

    /// <summary>
    /// Reserves <paramref name="quantity"/> units, returning false rather than throwing
    /// when stock is insufficient. Callers surface false as a 409, not a 500.
    /// </summary>
    public bool TryReserve(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Reservation quantity must be positive.");
        if (Available < quantity) return false;
        Reserved += quantity;
        return true;
    }

    /// <summary>Releases a reservation that expired or whose order was cancelled.</summary>
    public void Release(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Release quantity must be positive.");
        if (quantity > Reserved) throw new DomainException("Cannot release more than is reserved.");
        Reserved -= quantity;
    }

    /// <summary>Converts a reservation into a shipment: stock leaves the building.</summary>
    public void Ship(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Ship quantity must be positive.");
        if (quantity > Reserved) throw new DomainException("Cannot ship more than is reserved.");
        Reserved -= quantity;
        OnHand -= quantity;
    }

    public void Restock(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Restock quantity must be positive.");
        OnHand += quantity;
    }
}
