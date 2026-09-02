using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Carts;

/// <summary>
/// A shopper's cart, scoped to a signed demo-session cookie rather than a user account.
/// Every read is filtered by <see cref="DemoSessionId"/>, so two browsers on the live
/// demo never see each other's data.
/// </summary>
public sealed class Cart : Entity
{
    private readonly List<CartLine> _lines = [];

    private Cart() { } // EF

    public Cart(Guid demoSessionId, string currency = Money.DefaultCurrency)
    {
        DemoSessionId = demoSessionId;
        Currency = currency.ToUpperInvariant();
    }

    public Guid DemoSessionId { get; private set; }
    public string Currency { get; private set; } = Money.DefaultCurrency;
    public IReadOnlyList<CartLine> Lines => _lines;

    public bool IsEmpty => _lines.Count == 0;
    public int TotalQuantity => _lines.Sum(l => l.Quantity);

    /// <summary>Line totals at the price captured when each line was added.</summary>
    public Money Subtotal => _lines.Count == 0
        ? Money.Zero(Currency)
        : _lines.Select(l => l.LineTotal).Aggregate(static (a, b) => a + b);

    /// <summary>
    /// Adds a variant, merging with an existing line rather than duplicating it.
    /// The unit price is captured here and revalidated at checkout.
    /// </summary>
    public CartLine AddItem(Guid variantId, string sku, string displayName, Money unitPrice, int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be positive.");
        if (!string.Equals(unitPrice.Currency, Currency, StringComparison.Ordinal))
            throw new DomainException($"Cart is in {Currency}; cannot add a {unitPrice.Currency} item.");

        var existing = _lines.FirstOrDefault(l => l.VariantId == variantId);
        if (existing is not null)
        {
            existing.ChangeQuantity(existing.Quantity + quantity);
            return existing;
        }

        var line = new CartLine(Id, variantId, sku, displayName, unitPrice, quantity);
        _lines.Add(line);
        return line;
    }

    public void ChangeQuantity(Guid variantId, int quantity)
    {
        var line = _lines.FirstOrDefault(l => l.VariantId == variantId)
                   ?? throw new DomainException("That item is not in the cart.");

        if (quantity == 0) { _lines.Remove(line); return; }
        line.ChangeQuantity(quantity);
    }

    public void RemoveItem(Guid variantId) =>
        _lines.RemoveAll(l => l.VariantId == variantId);

    public void Clear() => _lines.Clear();
}
