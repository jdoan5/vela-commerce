namespace VelaCommerce.Domain.Payments;

/// <summary>
/// Why a gateway said no, reduced to the handful of answers a storefront can act on.
/// <para>
/// Real gateways return dozens of codes; mapping them down to these at the adapter boundary is
/// deliberate. The checkout page needs to decide one thing — "can this shopper usefully try
/// again?" — and a free-text code from a processor cannot be switched on without the switch
/// rotting the first time the processor adds a code.
/// </para>
/// <para>
/// There is no <c>None</c> member. A decline always has a reason, and every other outcome has
/// none at all, so the absence is modelled by the property being <see langword="null"/> rather
/// than by a zero value that would be legal to combine with <see cref="PaymentOutcome.Succeeded"/>.
/// </para>
/// </summary>
public enum PaymentDeclineReason
{
    /// <summary>The instrument is valid but cannot cover the amount. Retrying with another card may work.</summary>
    InsufficientFunds = 0,

    /// <summary>The card is past its expiry date. Retrying with the same card cannot work.</summary>
    ExpiredCard = 1,

    /// <summary>The issuer refused without saying why. The single most common real-world decline.</summary>
    DoNotHonor = 2,

    /// <summary>Blocked by risk rules. Deliberately not explained to the shopper in any detail.</summary>
    SuspectedFraud = 3,

    /// <summary>The gateway itself was unreachable or errored. The only reason here worth an automatic retry.</summary>
    ProcessorUnavailable = 4
}
