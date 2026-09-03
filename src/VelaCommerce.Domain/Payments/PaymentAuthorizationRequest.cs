using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Payments;

/// <summary>
/// Everything a gateway needs to take money once, and nothing else.
/// <para>
/// The absences are the design. There is no card number, no token and no shopper here: the
/// instrument is collected by the gateway's own UI, so this process never handles PAN data and
/// never inherits the compliance surface that comes with it. There is no <c>Order</c> either —
/// the port takes a reference, not an aggregate, so a gateway adapter can never reach into the
/// order and change it.
/// </para>
/// <para>
/// <see cref="RequestedAt"/> is a parameter rather than a clock read, matching
/// <c>Order.FromCart</c> and <c>Order.MarkPaid</c>. That is what makes a gateway reproducible:
/// the simulator stamps and signs its settlement events from this instant, so replaying a
/// checkout with the same inputs produces byte-identical payloads and signatures, and a test can
/// assert on them without freezing time globally.
/// </para>
/// </summary>
public sealed record PaymentAuthorizationRequest
{
    /// <summary>
    /// Builds a validated request. Throws rather than returning a flawed one, because every field
    /// here is either supplied by our own checkout handler or already validated at the API edge —
    /// a violation is a bug in this codebase, not bad input from a shopper.
    /// </summary>
    /// <param name="amount">The order total to authorize. Must be positive and in the order's currency.</param>
    /// <param name="orderReference">The human-facing order number, echoed back on every settlement event.</param>
    /// <param name="idempotencyKey">
    /// The same key the checkout used to defeat double-submit. Passing it through is what lets a
    /// gateway collapse two authorizations of one order into a single charge, and what lets the
    /// simulator derive a stable <see cref="PaymentAuthorizationResult.GatewayReference"/>.
    /// </param>
    /// <param name="requestedAt">When the checkout handler read the clock. Not read here.</param>
    /// <param name="scenarioHint">
    /// An optional, gateway-specific instruction. Opaque to the domain by design: the port must
    /// not learn the simulator's vocabulary, or swapping in a real gateway would mean changing
    /// this type. The simulator interprets it; a real adapter ignores it.
    /// </param>
    public PaymentAuthorizationRequest(
        Money amount,
        string orderReference,
        string idempotencyKey,
        DateTimeOffset requestedAt,
        string? scenarioHint = null)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException($"Cannot authorize {amount}: a payment must be for a positive amount.");
        if (string.IsNullOrWhiteSpace(orderReference))
            throw new DomainException("An order reference is required to authorize a payment.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("An idempotency key is required to authorize a payment.");

        Amount = amount;
        OrderReference = orderReference.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        RequestedAt = requestedAt;

        // Normalised to null so that "", "   " and absent are one state downstream. A hint that
        // survives as whitespace is a hint that will fail to match any scenario and silently
        // fall through to the default, which is the least debuggable outcome available.
        ScenarioHint = string.IsNullOrWhiteSpace(scenarioHint) ? null : scenarioHint.Trim();
    }

    /// <summary>The amount to take, in minor units. Equal to the order total, never a line or a subtotal.</summary>
    public Money Amount { get; }

    /// <summary>The order number this payment belongs to.</summary>
    public string OrderReference { get; }

    /// <summary>The checkout's idempotency key, carried through to the gateway.</summary>
    public string IdempotencyKey { get; }

    /// <summary>The instant the checkout handler is operating at. Gateways stamp their events with it.</summary>
    public DateTimeOffset RequestedAt { get; }

    /// <summary>
    /// Optional gateway-specific instruction, or <see langword="null"/>. Meaningless to the domain.
    /// </summary>
    public string? ScenarioHint { get; }
}
