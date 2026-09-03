using VelaCommerce.Domain.Payments;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// Everything the simulator decided about one authorization: the answer the domain port sees, and
/// the settlement notifications that should follow it.
/// <para>
/// The notifications are returned rather than delivered because delivery is somebody else's
/// transaction. The checkout is mid-flight when this is produced — the order row is not committed
/// yet — so posting a webhook here would race the insert and let a settlement arrive for an order
/// that does not exist. Handing back a plan lets the caller enqueue it in the same
/// <c>SaveChangesAsync</c> as the order, which is the outbox pattern the rest of this system
/// already uses.
/// </para>
/// </summary>
/// <param name="Authorization">What <see cref="IPaymentGateway.AuthorizeAsync"/> returns to the domain.</param>
/// <param name="Notifications">
/// Zero, one or two signed notifications, in the order they were <em>raised</em>. Delivery order
/// is whatever <see cref="SignedPaymentNotification.DeliverAfter"/> produces, which for the
/// <see cref="PaymentSimulatorScenario.Reorder"/> scenario is deliberately the reverse.
/// </param>
public sealed record SimulatedAuthorization(
    PaymentAuthorizationResult Authorization,
    IReadOnlyList<SignedPaymentNotification> Notifications);
