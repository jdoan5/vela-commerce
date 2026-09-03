using System.Text;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// One settlement notification, signed and ready to post — payload, header and the delay before
/// it should be delivered.
/// <para>
/// The simulator produces these rather than delivering them itself. That split is what keeps the
/// gateway synchronous, deterministic and free of a timer: authorizing a payment returns
/// immediately with a delivery plan, and whatever owns the outbox decides when the plan happens.
/// It also means a test can assert on the exact bytes and the exact signature without waiting for
/// anything, and can hand the payload straight to the receiver.
/// </para>
/// </summary>
/// <param name="Event">The deserialized event, for logging and for tests. Never re-serialize this to verify.</param>
/// <param name="Payload">
/// The exact JSON that was signed. This string — not a re-serialization of <paramref name="Event"/> —
/// is what must be sent as the request body, because the signature covers these bytes.
/// </param>
/// <param name="Signature">
/// The full value for the <see cref="PaymentSignature.HeaderName"/> header, in the documented
/// <c>t=…,v1=…</c> shape.
/// </param>
/// <param name="DeliverAfter">
/// How long after the authorization this notification should be posted. Zero means immediately.
/// A relative delay rather than an absolute instant so that whoever delivers it does not have to
/// reason about which clock the simulator was using.
/// </param>
public sealed record SignedPaymentNotification(
    PaymentSettlementEvent Event,
    string Payload,
    string Signature,
    TimeSpan DeliverAfter)
{
    /// <summary>
    /// The payload as the bytes that were actually signed. Convenience for a deliverer building a
    /// request body, and a reminder that the encoding is UTF-8 and not the process default.
    /// </summary>
    public byte[] PayloadBytes() => Encoding.UTF8.GetBytes(Payload);
}
