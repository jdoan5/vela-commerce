namespace VelaCommerce.Domain.Payments;

/// <summary>
/// Taking money, expressed as a domain capability rather than as an SDK call.
/// <para>
/// This lives in the domain because "authorize this amount against this order" is a business
/// operation, and because the domain is the layer that must stay portable: it compiles against
/// the base class library alone, so there is no <c>HttpClient</c>, no Stripe type and no
/// <c>IConfiguration</c> anywhere in this file. An architecture test enforces that, which is what
/// stops a well-meaning adapter from leaking a gateway's own request object back through the port.
/// </para>
/// <para>
/// The default implementation is the in-repository simulator
/// (<c>VelaCommerce.Infrastructure.Payments.SimulatedPaymentGateway</c>), not a real processor.
/// That inversion is the whole point of the port: the demo's money path has no third-party
/// account behind it, so a rotated test key or a deprecated API version years from now cannot
/// break a link on a CV. A real gateway is added as a second implementation behind this
/// interface and selected by configuration; nothing above the port changes when it is.
/// </para>
/// <para>
/// One method, on purpose. Refunds, captures and voids are not here yet because nothing calls
/// them yet, and a port whose members are speculative is a port whose members are wrong.
/// </para>
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Attempts to take <see cref="PaymentAuthorizationRequest.Amount"/> for the referenced order.
    /// <para>
    /// Returns a result rather than throwing on a decline: a shopper's card being refused is a
    /// normal business answer, and modelling it as an exception makes the ordinary path of a
    /// checkout run through a catch block. Exceptions from this method mean the gateway could not
    /// be asked at all — a network fault, a misconfiguration — and are the caller's cue to retry
    /// or to fail the request, never to tell the shopper their card was declined.
    /// </para>
    /// <para>
    /// Implementations must be idempotent on
    /// <see cref="PaymentAuthorizationRequest.IdempotencyKey"/>: calling twice with the same key
    /// must take the money once and return the same
    /// <see cref="PaymentAuthorizationResult.GatewayReference"/> both times. The checkout's unique
    /// index already stops a second order being created, but the gateway call happens before that
    /// insert commits, so this is the layer that has to survive a double submit.
    /// </para>
    /// </summary>
    Task<PaymentAuthorizationResult> AuthorizeAsync(
        PaymentAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
