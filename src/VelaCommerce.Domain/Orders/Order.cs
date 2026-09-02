using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Orders;

/// <summary>
/// The checkout aggregate.
/// <para>
/// Two invariants matter more than the rest, and both are also enforced in PostgreSQL
/// rather than trusted to this class alone:
/// </para>
/// <list type="number">
/// <item>A double-submitted checkout creates one order, not two. The client supplies an
/// <see cref="IdempotencyKey"/> and a unique index makes the second insert lose.</item>
/// <item>Refunds never exceed what was captured. A CHECK constraint backs
/// <see cref="Refund"/>.</item>
/// </list>
/// </summary>
public sealed class Order : Entity
{
    private readonly List<OrderLine> _lines = [];

    private Order() { } // EF

    private Order(
        Guid demoSessionId,
        string orderNumber,
        string idempotencyKey,
        ShippingAddress address,
        string currency,
        DateTimeOffset placedAt)
    {
        DemoSessionId = demoSessionId;
        OrderNumber = orderNumber;
        IdempotencyKey = idempotencyKey;
        ShippingAddress = address;
        Currency = currency;
        Status = OrderStatus.Pending;
        PlacedAt = placedAt;
    }

    public Guid DemoSessionId { get; private set; }

    /// <summary>Human-facing reference shown on the confirmation page.</summary>
    public string OrderNumber { get; private set; } = null!;

    /// <summary>Client-supplied key; unique per demo session. This is what defeats double-submit.</summary>
    public string IdempotencyKey { get; private set; } = null!;

    public OrderStatus Status { get; private set; }
    public string Currency { get; private set; } = Money.DefaultCurrency;
    public ShippingAddress ShippingAddress { get; private set; } = null!;
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public Money Subtotal => _lines.Count == 0
        ? Money.Zero(Currency)
        : _lines.Select(l => l.LineTotal).Aggregate(static (a, b) => a + b);

    public Money Shipping { get; private set; }
    public Money Tax { get; private set; }
    public Money Total => Subtotal + Shipping + Tax;

    /// <summary>Amount actually captured by the payment gateway.</summary>
    public Money Captured { get; private set; }

    /// <summary>Running total of refunds. Never allowed to exceed <see cref="Captured"/>.</summary>
    public Money Refunded { get; private set; }

    public Money RefundableRemaining => Captured - Refunded;

    /// <summary>
    /// Builds an order from a cart. Prices come from the cart lines, which the caller
    /// must already have revalidated against the live catalog.
    /// <para>
    /// Time is a parameter, never <c>DateTimeOffset.UtcNow</c>. The demo runs an
    /// accelerated order timeline and the tests assert on exact timestamps, so an
    /// aggregate that reads the ambient clock cannot be driven or verified. An
    /// architecture test enforces this across the whole solution.
    /// </para>
    /// </summary>
    public static Order FromCart(
        Cart cart,
        string orderNumber,
        string idempotencyKey,
        ShippingAddress address,
        Money shipping,
        Money tax,
        DateTimeOffset placedAt)
    {
        if (cart.IsEmpty) throw new DomainException("Cannot place an order for an empty cart.");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new DomainException("An idempotency key is required.");
        address.Validate();

        var order = new Order(cart.DemoSessionId, orderNumber, idempotencyKey, address, cart.Currency, placedAt)
        {
            Shipping = shipping,
            Tax = tax,
            Captured = Money.Zero(cart.Currency),
            Refunded = Money.Zero(cart.Currency)
        };

        foreach (var line in cart.Lines)
            order._lines.Add(new OrderLine(order.Id, line.VariantId, line.Sku, line.DisplayName, line.UnitPrice, line.Quantity));

        return order;
    }

    private void Transition(OrderStatus to)
    {
        if (!OrderStateMachine.IsLegal(Status, to))
            throw new DomainException($"Illegal order transition {Status} -> {to}.");
        Status = to;
    }

    /// <summary>
    /// Settles payment. Deliberately not idempotent by itself: duplicate webhook
    /// suppression belongs to the receiver, which records the event id and this
    /// transition in one transaction.
    /// </summary>
    public void MarkPaid(Money captured, DateTimeOffset now)
    {
        if (captured.IsNegative) throw new DomainException("Captured amount cannot be negative.");
        if (captured != Total)
            throw new DomainException($"Captured {captured} does not match order total {Total}.");

        Transition(OrderStatus.Paid);
        Captured = captured;
        PaidAt = now;
    }

    public void MarkPacked() => Transition(OrderStatus.Packed);

    public void MarkShipped() => Transition(OrderStatus.Shipped);

    public void Cancel() => Transition(OrderStatus.Cancelled);

    /// <summary>Refunds part or all of the captured amount.</summary>
    public void Refund(Money amount)
    {
        if (amount.IsNegative || amount.IsZero) throw new DomainException("Refund amount must be positive.");
        if (Status is not (OrderStatus.Paid or OrderStatus.Packed or OrderStatus.Shipped))
            throw new DomainException($"Cannot refund an order that is {Status}.");
        if (amount > RefundableRemaining)
            throw new DomainException($"Refund of {amount} exceeds the remaining {RefundableRemaining}.");

        Refunded += amount;
    }
}
