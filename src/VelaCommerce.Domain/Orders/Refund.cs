using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Orders;

/// <summary>
/// One movement of money back to the shopper.
/// <para>
/// The order already carried a <see cref="Order.Refunded"/> running total, and a running total is
/// not an answer to any question worth asking after the fact. "Did we refund this twice?" and "who
/// authorised the second one?" and "the gateway shows two refunds, which of ours are they?" are all
/// unanswerable from a scalar that only knows how much. This is the ledger the scalar summarises,
/// and <see cref="Order.Refunded"/> is now a cached fold of it — a fold an integration test checks,
/// because a total that can disagree with its own rows is worse than no total.
/// </para>
/// <para>
/// <b>A row here means the money actually moved.</b> It is written only after the gateway has
/// confirmed the refund, never before and never speculatively, so the ledger cannot claim a
/// payment that a failed gateway call did not make. That ordering is the whole reason the refund
/// handler is shaped the way it is.
/// </para>
/// </summary>
public sealed class Refund : Entity
{
    private Refund() { } // EF

    /// <summary>
    /// Internal because a refund is only ever created by <see cref="Order.IssueRefund"/>, which is
    /// where the amount is checked against what is left. A refund constructed on its own could be
    /// for more than the order ever captured, and would be persisted before anything noticed.
    /// </summary>
    internal Refund(
        Guid orderId,
        Money amount,
        RefundReason reason,
        string idempotencyKey,
        string gatewayReference,
        int restockedUnits,
        DateTimeOffset refundedAt)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("A refund must be for a positive amount.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("A refund requires an idempotency key; without one a retry becomes a second refund.");
        if (string.IsNullOrWhiteSpace(gatewayReference))
            throw new DomainException("A refund requires the gateway's reference; without one the money cannot be traced.");
        if (restockedUnits < 0)
            throw new DomainException("Restocked units cannot be negative.");

        OrderId = orderId;
        Amount = amount;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
        GatewayReference = gatewayReference;
        RestockedUnits = restockedUnits;
        RefundedAt = refundedAt;
    }

    public Guid OrderId { get; private set; }

    /// <summary>How much went back. Positive, and in the order's currency.</summary>
    public Money Amount { get; private set; }

    public RefundReason Reason { get; private set; }

    /// <summary>
    /// The caller's key for this refund, unique within the order. A unique index enforces that, so
    /// a retried request loses in the database rather than being trusted to lose in C#.
    /// </summary>
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>
    /// The gateway's own identifier for the refund — not the authorization's. This is the string
    /// that joins our ledger row to the provider's dashboard when the two are being reconciled.
    /// </summary>
    public string GatewayReference { get; private set; } = null!;

    /// <summary>
    /// Units this refund put back on the shelf, which is zero for most refunds.
    /// <para>
    /// Stock comes back only when a cancellation catches an order whose parcel has not left, and
    /// recording the count here rather than inferring it later keeps the two halves of the decision
    /// — the money and the goods — in one row that either happened or did not.
    /// </para>
    /// </summary>
    public int RestockedUnits { get; private set; }

    /// <summary>When the refund was recorded. A parameter at every level, never a clock read.</summary>
    public DateTimeOffset RefundedAt { get; private set; }
}
