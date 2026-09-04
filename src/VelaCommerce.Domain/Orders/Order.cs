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
/// <see cref="IssueRefund"/>, and a unique index on the refund's idempotency key backs the
/// suppression of a retried one.</item>
/// </list>
/// </summary>
public sealed class Order : Entity
{
    private readonly List<OrderLine> _lines = [];
    private readonly List<Refund> _refunds = [];

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

    /// <summary>
    /// The gateway's reference for the payment that settled this order, or null while it is unpaid.
    /// <para>
    /// Recorded because a refund is issued against a payment, not against an order: without this
    /// the system could take money and then have no way to name what it had taken, and refunding
    /// would mean recomputing a reference from the simulator's own hashing rule — which works
    /// exactly until a real gateway is plugged in behind the port, and then silently refunds
    /// nothing. It is also the string a support conversation quotes.
    /// </para>
    /// </summary>
    public string? PaymentReference { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>
    /// Every refund on this order, oldest first once loaded. <see cref="Refunded"/> is the fold of
    /// their amounts, kept as a column because the CHECK constraint that stops an over-refund has
    /// to compare against something the database can see without summing a child table.
    /// </summary>
    public IReadOnlyList<Refund> Refunds => _refunds;

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
    /// architecture test enforces this across the domain, infrastructure and API assemblies.
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
    /// <param name="captured">What the gateway took. Must equal <see cref="Total"/> to the cent.</param>
    /// <param name="paymentReference">
    /// The gateway's identifier for the payment. Required, because an order that captured money it
    /// cannot name is an order that can never be refunded.
    /// </param>
    /// <param name="now">The caller's instant.</param>
    public void MarkPaid(Money captured, string paymentReference, DateTimeOffset now)
    {
        if (captured.IsNegative) throw new DomainException("Captured amount cannot be negative.");
        if (captured != Total)
            throw new DomainException($"Captured {captured} does not match order total {Total}.");
        if (string.IsNullOrWhiteSpace(paymentReference))
            throw new DomainException(
                "A payment reference is required to settle an order. Without one the capture cannot "
                + "be refunded later, because a refund is issued against a payment rather than an order.");

        Transition(OrderStatus.Paid);
        Captured = captured;
        PaymentReference = paymentReference;
        PaidAt = now;
    }


    public void MarkPacked() => Transition(OrderStatus.Packed);

    public void MarkShipped() => Transition(OrderStatus.Shipped);

    /// <summary>
    /// Cancels an order that owes the shopper nothing.
    /// <para>
    /// <b>This refuses to cancel an order with money still on it</b>, and that refusal is the point.
    /// The state machine has always had a <c>Paid -&gt; Cancelled</c> edge, and taking it used to
    /// leave an order that was terminal, had captured funds, and could never refund them —
    /// <see cref="IssueRefund"/> rejects a Cancelled order, so the money was not merely unreturned
    /// but unreturnable. A test named the gap deliberately rather than hiding it; this is the rule
    /// that closes it.
    /// </para>
    /// <para>
    /// Callers that mean to cancel a paid order want <see cref="CancelAndRefund"/>, which does both
    /// or neither. Three production callers reach this method. Two are safe by construction — the
    /// settlement handler cancelling a decline and the reservation reaper cancelling a lapsed
    /// checkout both act on Pending orders that captured nothing. The third is the cancellation
    /// endpoint, which reaches here only on the branch where nothing is outstanding, having
    /// returned the money first; a fully refunded Paid order is legally cancellable and this guard
    /// is what lets it through while still refusing one that owes.
    /// </para>
    /// </summary>
    public void Cancel()
    {
        if (!RefundableRemaining.IsZero)
            throw new DomainException(
                $"Cannot cancel an order still holding {RefundableRemaining} of captured funds. "
                + "Cancelling would strand money that no later refund could reach, because a "
                + "cancelled order refuses refunds. Use CancelAndRefund instead.");

        Transition(OrderStatus.Cancelled);
    }

    /// <summary>
    /// Returns everything still outstanding and cancels the order, as one indivisible act.
    /// <para>
    /// One method rather than two calls, so the two facts cannot come apart. A caller that refunded
    /// and then failed to cancel leaves an order that is paid for and refunded — which reads as a
    /// free parcel — and a caller that cancelled first cannot refund at all. The order of operations
    /// inside matters for the same reason and is not incidental: the money goes back first, because
    /// a refund is the step that can fail.
    /// </para>
    /// <para>
    /// Always the full remaining amount. A partial refund alongside a cancellation would leave an
    /// order that is finished and still owes, with no state left to represent the debt.
    /// </para>
    /// </summary>
    /// <param name="idempotencyKey">Unique within this order; a retry must not refund twice.</param>
    /// <param name="gatewayReference">The gateway's identifier for the refund it has already made.</param>
    /// <param name="restockedUnits">Units this cancellation put back on the shelf, or zero.</param>
    /// <param name="now">The caller's instant. Never read from the clock here.</param>
    public Refund CancelAndRefund(
        string idempotencyKey,
        string gatewayReference,
        int restockedUnits,
        DateTimeOffset now)
    {
        // Checked before anything moves, so a cancellation that the state machine was going to
        // refuse does not first hand back money against an order that stays open.
        if (!OrderStateMachine.IsLegal(Status, OrderStatus.Cancelled))
            throw new DomainException($"Illegal order transition {Status} -> {OrderStatus.Cancelled}.");

        var refund = IssueRefund(
            RefundableRemaining, RefundReason.Cancellation, idempotencyKey, gatewayReference, restockedUnits, now);

        Transition(OrderStatus.Cancelled);
        return refund;
    }

    /// <summary>
    /// Records money that has already gone back to the shopper.
    /// <para>
    /// <b>Past tense on purpose.</b> This does not ask a gateway for anything — the domain has no
    /// way to, and should not: the caller performs the refund and then records it here, so a
    /// gateway call that fails leaves no ledger row claiming a payment that never happened. The
    /// inverse ordering would be worse in the one way that matters, since a row that says the money
    /// went back is what a shopper's next email will be answered from.
    /// </para>
    /// <para>
    /// Not idempotent by itself, and deliberately so — matching <see cref="MarkPaid"/>. Suppressing
    /// a retried refund needs the key to be checked and the row written under one lock, which is a
    /// transaction the aggregate cannot open. <see cref="IdempotencyKey"/> is carried here so the
    /// database can enforce with a unique index what this method cannot.
    /// </para>
    /// </summary>
    /// <param name="amount">How much went back. Positive, and no more than <see cref="RefundableRemaining"/>.</param>
    /// <param name="reason">Why, for the ledger.</param>
    /// <param name="idempotencyKey">The caller's key, unique within this order.</param>
    /// <param name="gatewayReference">The gateway's own identifier for the refund.</param>
    /// <param name="restockedUnits">Units returned to the shelf as part of this refund, usually zero.</param>
    /// <param name="now">The caller's instant.</param>
    public Refund IssueRefund(
        Money amount,
        RefundReason reason,
        string idempotencyKey,
        string gatewayReference,
        int restockedUnits,
        DateTimeOffset now)
    {
        if (amount.IsNegative || amount.IsZero) throw new DomainException("Refund amount must be positive.");

        // Shipped is refundable and Cancelled is not, which is the same distinction seen from two
        // sides: a parcel in transit is a sale that can be undone in money even though the goods
        // are gone, whereas a cancelled order has already had its money settled one way or another.
        if (Status is not (OrderStatus.Paid or OrderStatus.Packed or OrderStatus.Shipped))
            throw new DomainException($"Cannot refund an order that is {Status}.");

        if (amount > RefundableRemaining)
            throw new DomainException($"Refund of {amount} exceeds the remaining {RefundableRemaining}.");

        var refund = new Refund(Id, amount, reason, idempotencyKey, gatewayReference, restockedUnits, now);
        _refunds.Add(refund);
        Refunded += amount;

        return refund;
    }

    /// <summary>
    /// True when every captured cent has been returned. Distinct from being cancelled: a fully
    /// refunded order keeps the status its fulfilment actually reached, because a parcel that
    /// shipped did ship regardless of who ended up paying for it.
    /// </summary>
    public bool IsFullyRefunded => !Captured.IsZero && RefundableRemaining.IsZero;
}
