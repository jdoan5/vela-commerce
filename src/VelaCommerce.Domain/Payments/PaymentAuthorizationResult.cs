using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Payments;

/// <summary>
/// What the gateway said, in a shape where the impossible answers cannot be written down.
/// <para>
/// The constructor is private and the four factories below are the only way in, so a result is
/// always internally consistent: a decline carries a reason and nothing else does; a deferred
/// authorization carries a correlation id and nothing else does. Without that, the type would be
/// five nullable properties and a comment, and the first caller to build one by hand would
/// produce a "succeeded" result with a decline reason attached — which reads fine, persists
/// fine, and is only wrong on the reporting screen six weeks later.
/// </para>
/// <para>
/// Deliberately not an abstract hierarchy of one record per outcome. The result crosses a
/// serialization boundary (it is logged, and its outcome is persisted on the order), and a
/// closed enum plus guarded factories survives that trip; a polymorphic hierarchy needs a
/// discriminator to survive it, which is the enum again with extra steps.
/// </para>
/// </summary>
public sealed record PaymentAuthorizationResult
{
    private PaymentAuthorizationResult(
        PaymentOutcome outcome,
        string gatewayReference,
        Money amount,
        PaymentDeclineReason? declineReason,
        string? settlementCorrelationId)
    {
        if (string.IsNullOrWhiteSpace(gatewayReference))
            throw new DomainException("A gateway result must carry a reference; without one the payment cannot be traced.");

        // The two cross-field invariants, asserted once here rather than trusted to four factories.
        if ((outcome == PaymentOutcome.Declined) != declineReason.HasValue)
            throw new DomainException($"A {outcome} result must {(outcome == PaymentOutcome.Declined ? "carry" : "not carry")} a decline reason.");

        var deferred = outcome == PaymentOutcome.PendingSettlement;
        if (deferred == string.IsNullOrWhiteSpace(settlementCorrelationId))
            throw new DomainException($"A {outcome} result must {(deferred ? "carry" : "not carry")} a settlement correlation id.");

        Outcome = outcome;
        GatewayReference = gatewayReference;
        Amount = amount;
        DeclineReason = declineReason;
        SettlementCorrelationId = settlementCorrelationId;
    }

    /// <summary>How the attempt ended.</summary>
    public PaymentOutcome Outcome { get; }

    /// <summary>
    /// The gateway's own identifier for this attempt, stable across retries of the same
    /// idempotency key. This is the string to put in a support ticket, and the join key between
    /// our order and the gateway's dashboard.
    /// </summary>
    public string GatewayReference { get; }

    /// <summary>
    /// The amount captured when <see cref="Outcome"/> is <see cref="PaymentOutcome.Succeeded"/>,
    /// and the amount that was attempted otherwise. Carried on every outcome so that a failed
    /// attempt is still auditable against the order total it was meant to settle.
    /// </summary>
    public Money Amount { get; }

    /// <summary>Populated when and only when the attempt was declined.</summary>
    public PaymentDeclineReason? DeclineReason { get; }

    /// <summary>
    /// Populated when and only when settlement is deferred. The webhook receiver looks events up
    /// by this value, so it must be recorded on the order at the moment the result is returned —
    /// not derived later, when the shape of the reference may have changed.
    /// </summary>
    public string? SettlementCorrelationId { get; }

    /// <summary>True when the money has actually moved and the order may be marked paid now.</summary>
    public bool IsCaptured => Outcome == PaymentOutcome.Succeeded;

    /// <summary>True when the order must stay Pending until a signed webhook arrives.</summary>
    public bool AwaitsSettlement => Outcome == PaymentOutcome.PendingSettlement;

    /// <summary>The gateway took <paramref name="captured"/> synchronously. No webhook is expected.</summary>
    public static PaymentAuthorizationResult Succeeded(string gatewayReference, Money captured) =>
        new(PaymentOutcome.Succeeded, gatewayReference, captured, declineReason: null, settlementCorrelationId: null);

    /// <summary>The gateway refused. <paramref name="attempted"/> is what it refused to take.</summary>
    public static PaymentAuthorizationResult Declined(string gatewayReference, Money attempted, PaymentDeclineReason reason) =>
        new(PaymentOutcome.Declined, gatewayReference, attempted, reason, settlementCorrelationId: null);

    /// <summary>The shopper never completed the payment. Nothing was taken and nothing will be.</summary>
    public static PaymentAuthorizationResult Abandoned(string gatewayReference, Money attempted) =>
        new(PaymentOutcome.Abandoned, gatewayReference, attempted, declineReason: null, settlementCorrelationId: null);

    /// <summary>
    /// The gateway accepted the request but will settle asynchronously;
    /// <paramref name="settlementCorrelationId"/> is the handle the later webhook will carry.
    /// </summary>
    public static PaymentAuthorizationResult PendingSettlement(
        string gatewayReference,
        Money attempted,
        string settlementCorrelationId) =>
        new(PaymentOutcome.PendingSettlement, gatewayReference, attempted, declineReason: null, settlementCorrelationId);
}
