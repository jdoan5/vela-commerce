using VelaCommerce.Domain.Payments;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// The simulator's extra half, kept off the domain port.
/// <para>
/// <see cref="IPaymentGateway"/> deliberately says nothing about webhooks: a real gateway sends
/// them from its own infrastructure, and a port that returned a delivery plan would be describing
/// our simulator rather than the concept of taking money. So the code that has to enqueue
/// simulated notifications — the checkout handler and, later, the outbox worker — depends on this
/// interface instead, and only ever resolves it when the simulator is the configured gateway.
/// </para>
/// <para>
/// Synchronous on purpose. There is no I/O here, and a <c>Task</c> would invite a caller to
/// believe there was.
/// </para>
/// </summary>
public interface IPaymentSimulator
{
    /// <summary>
    /// Decides the outcome for a request and signs whatever notifications should follow it.
    /// A pure function of the request and the configured options: same input, same bytes, same
    /// signature, every time.
    /// </summary>
    SimulatedAuthorization Simulate(PaymentAuthorizationRequest request);
}
