namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// Why a settlement notification was or was not accepted.
/// <para>
/// <see cref="PaymentSignature.Verify"/> returns this rather than a <see cref="bool"/> because the
/// three failures need three different responses from the receiver, and collapsing them into
/// <c>false</c> is how a webhook endpoint ends up retrying forever on a payload it will never
/// accept. A gateway retries on 5xx and gives up on 4xx, so the receiver must be able to tell a
/// forged signature (4xx, stop) from a malformed header (4xx, stop) from a stale timestamp
/// (4xx, but worth an alert — either someone is replaying us or a clock has drifted).
/// </para>
/// </summary>
public enum PaymentSignatureResult
{
    /// <summary>Signature matches and the timestamp is inside the tolerance. Process the event.</summary>
    Valid = 0,

    /// <summary>The header is absent, or not in the documented <c>t=…,v1=…</c> shape.</summary>
    Malformed = 1,

    /// <summary>
    /// Well-formed, but the timestamp is outside the tolerance window. Almost always a replay of
    /// a signature captured from a log; occasionally a genuinely skewed clock.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// Well-formed and timely, but the HMAC does not match. Either the payload was altered in
    /// flight or the sender does not hold the shared secret. Never log the supplied signature at
    /// information level; it is an oracle for anyone probing the endpoint.
    /// </summary>
    Mismatched = 3
}
