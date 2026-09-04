using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Payments;

/// <summary>
/// Everything a gateway needs to give money back once, and nothing else.
/// <para>
/// It carries the <see cref="AuthorizationReference"/> rather than an order, because a refund is
/// against a payment, not against a purchase. That is not a distinction of taste: a gateway has no
/// concept of our order, and asking it to refund "order VELA-XXXXXXX" would require this side to
/// keep a mapping the gateway already keeps better.
/// </para>
/// <para>
/// <see cref="IdempotencyKey"/> is the refund's own key, never the checkout's. Reusing the
/// checkout's would make a refund indistinguishable from the authorization it reverses, and would
/// collapse two genuinely different refunds of the same order into one.
/// </para>
/// </summary>
public sealed record PaymentRefundRequest
{
    /// <summary>Builds a validated request. Throws on a violation, which is a bug here rather than bad input.</summary>
    /// <param name="amount">How much to return. Positive, and never more than the order has left to refund.</param>
    /// <param name="authorizationReference">The gateway's reference for the payment being reversed.</param>
    /// <param name="orderReference">The human-facing order number, for the gateway's own records and our logs.</param>
    /// <param name="idempotencyKey">This refund's key. Two calls carrying it must move the money once.</param>
    /// <param name="requestedAt">When the handler read the clock. Not read here.</param>
    /// <param name="scenarioHint">
    /// Optional, gateway-specific, and opaque to the domain — exactly as on
    /// <see cref="PaymentAuthorizationRequest"/>. The simulator reads it; a real adapter ignores it.
    /// </param>
    public PaymentRefundRequest(
        Money amount,
        string authorizationReference,
        string orderReference,
        string idempotencyKey,
        DateTimeOffset requestedAt,
        string? scenarioHint = null)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException($"Cannot refund {amount}: a refund must be for a positive amount.");
        if (string.IsNullOrWhiteSpace(authorizationReference))
            throw new DomainException("A refund must name the payment it reverses.");
        if (string.IsNullOrWhiteSpace(orderReference))
            throw new DomainException("An order reference is required to refund a payment.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("An idempotency key is required to refund a payment.");

        Amount = amount;
        AuthorizationReference = authorizationReference.Trim();
        OrderReference = orderReference.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        RequestedAt = requestedAt;
        ScenarioHint = string.IsNullOrWhiteSpace(scenarioHint) ? null : scenarioHint.Trim();
    }

    /// <summary>The amount to return, in minor units.</summary>
    public Money Amount { get; }

    /// <summary>The gateway's reference for the original payment.</summary>
    public string AuthorizationReference { get; }

    /// <summary>The order number this refund belongs to.</summary>
    public string OrderReference { get; }

    /// <summary>This refund's idempotency key, distinct from the checkout's.</summary>
    public string IdempotencyKey { get; }

    /// <summary>The instant the handler is operating at.</summary>
    public DateTimeOffset RequestedAt { get; }

    /// <summary>Optional gateway-specific instruction, or <see langword="null"/>.</summary>
    public string? ScenarioHint { get; }
}
