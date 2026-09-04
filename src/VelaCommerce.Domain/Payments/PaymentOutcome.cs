namespace VelaCommerce.Domain.Payments;

/// <summary>
/// Every way an authorization attempt can end. A closed set on purpose: the checkout handler
/// switches on this, and a gateway that could invent a fifth answer would leave that switch
/// silently incomplete.
/// <para>
/// The distinction that actually matters is between <see cref="Succeeded"/> and
/// <see cref="PendingSettlement"/>. Both are "nothing went wrong", but only the first one means
/// the money has moved. A checkout that treats them alike either marks an order paid before the
/// funds exist, or leaves a genuinely paid order sitting in Pending forever — which is exactly
/// the bug a webhook-driven gateway exists to create.
/// </para>
/// <para>
/// Values are explicit as a habit for an enum crossing a serialization boundary. It is NOT
/// persisted today — no column holds it, and the order carries the durable facts instead — so a
/// reorder would repaint nothing yet. Keep them explicit anyway: the day it is stored, a reorder
/// becomes silent and irreversible.
/// </para>
/// </summary>
public enum PaymentOutcome
{
    /// <summary>
    /// The gateway captured the full amount synchronously. The order may be marked paid in the
    /// same request, and no webhook is expected for this authorization.
    /// </summary>
    Succeeded = 0,

    /// <summary>
    /// The gateway refused. Terminal for this attempt, and the shopper is told why via
    /// <see cref="PaymentDeclineReason"/>. A retry is a new authorization with a new
    /// idempotency key, never a re-read of this result.
    /// </summary>
    Declined = 1,

    /// <summary>
    /// The shopper never finished — closed the tab, walked away from the hosted page. No money
    /// moved and none ever will under this reference. Distinct from <see cref="Declined"/>
    /// because nobody said no: the reservation should be released and the cart left intact.
    /// </summary>
    Abandoned = 2,

    /// <summary>
    /// Accepted, but settlement is asynchronous and arrives later as a signed webhook. The order
    /// stays Pending and the UI must say "confirming payment" rather than spin.
    /// <para>
    /// <see cref="PaymentAuthorizationResult.SettlementCorrelationId"/> is populated for exactly
    /// this case, and is what lets the receiver match an event that may arrive twice, out of
    /// order, or after the container has scaled to zero and come back.
    /// </para>
    /// </summary>
    PendingSettlement = 3
}
