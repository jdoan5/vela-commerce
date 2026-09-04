using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Payments;

/// <summary>
/// What the gateway said when asked to give money back.
/// <para>
/// Two outcomes, not four. An authorization can be deferred to a webhook, and a refund in this
/// system cannot: nothing here waits for a refund to settle asynchronously, so modelling a state
/// nothing produces would mean a branch nothing tests. If a real adapter later needs one, it
/// arrives with the caller that handles it.
/// </para>
/// <para>
/// A refusal comes back as a result rather than an exception, for the same reason a declined card
/// does: a gateway saying "not this one" is an answer. An exception from
/// <see cref="IPaymentGateway.RefundAsync"/> means the gateway could not be asked at all, and the
/// caller must not record a ledger row either way — the difference is that a refusal is worth
/// telling the shopper about and a fault is worth retrying.
/// </para>
/// </summary>
public sealed record PaymentRefundResult
{
    private PaymentRefundResult(bool succeeded, string gatewayReference, Money amount, string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(gatewayReference))
            throw new DomainException("A refund result must carry a reference; without one the money cannot be traced.");

        // The cross-field invariant, asserted once here rather than trusted to two factories: a
        // successful refund with a failure reason attached reads fine and is wrong on the
        // reconciliation screen weeks later.
        if (succeeded == (failureReason is not null))
            throw new DomainException(
                $"A {(succeeded ? "successful" : "failed")} refund must "
                + $"{(succeeded ? "not carry" : "carry")} a failure reason.");

        IsRefunded = succeeded;
        GatewayReference = gatewayReference;
        Amount = amount;
        FailureReason = failureReason;
    }

    /// <summary>True when the money has actually left our account and a ledger row may be written.</summary>
    public bool IsRefunded { get; }

    /// <summary>
    /// The gateway's identifier for the refund itself, distinct from the authorization's reference.
    /// Present on a refusal too, so a refund that was declined is still traceable.
    /// </summary>
    public string GatewayReference { get; }

    /// <summary>The amount returned, or the amount that was attempted when the refund failed.</summary>
    public Money Amount { get; }

    /// <summary>Populated when and only when the refund failed.</summary>
    public string? FailureReason { get; }

    public static PaymentRefundResult Succeeded(string gatewayReference, Money amount) =>
        new(succeeded: true, gatewayReference, amount, failureReason: null);

    public static PaymentRefundResult Failed(string gatewayReference, Money amount, string reason) =>
        new(succeeded: false, gatewayReference, amount, reason);
}
