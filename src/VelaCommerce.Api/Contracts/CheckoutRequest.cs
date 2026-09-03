namespace VelaCommerce.Api.Contracts;

/// <summary>
/// Body of <c>POST /api/checkout</c>.
/// <para>
/// <strong>What is absent is again the design.</strong> There is no cart id — the cart is whatever
/// the signed session cookie says it is, and an API that accepted one would have to decide whether
/// to trust it. There are no line items, no prices and no totals: the server reads the cart it
/// already holds, revalidates every price against the live catalog and computes the total itself,
/// so a client cannot check out at a price it invented or for goods it never added. And there is
/// no card number — the instrument belongs to the payment gateway's own surface, so this process
/// never touches PAN data.
/// </para>
/// </summary>
/// <param name="ShippingAddress">Where the order goes. Validated by the domain, not by this record.</param>
/// <param name="IdempotencyKey">
/// The client's key for this checkout attempt, unique per visitor. Optional here only because the
/// <c>Idempotency-Key</c> request header is the conventional place to put it and is accepted
/// instead; one of the two must be present. Supplying both is allowed as long as they agree, so a
/// client that sets the header and echoes it in the body is not punished for being explicit.
/// </param>
/// <param name="PaymentScenario">
/// An optional instruction for the payment simulator — <c>Succeed</c>, <c>Decline</c>,
/// <c>Abandon</c>, <c>Duplicate</c>, <c>Delay</c>, <c>Reorder</c>. Passed straight through to the
/// gateway port as an opaque hint, which a real gateway adapter would ignore. Left null, the
/// simulator falls back to the trailing cents of the order total, so the demo can be driven
/// without knowing this field exists.
/// </param>
public sealed record CheckoutRequest(
    CheckoutAddressRequest? ShippingAddress,
    string? IdempotencyKey = null,
    string? PaymentScenario = null);

/// <summary>
/// A postal address as it arrives on the wire.
/// <para>
/// Every field is nullable here even though the domain requires most of them. That is not laxity:
/// a missing field in JSON arrives as null whatever this record claims, so declaring them
/// non-nullable would only mean the compiler stops warning about a case that still happens. The
/// nulls are carried as far as <c>ShippingAddress.Validate()</c>, which is the single place that
/// decides what a usable address is — so the API and the order aggregate cannot come to different
/// conclusions about it, and the message the shopper sees is the domain's own wording.
/// </para>
/// </summary>
/// <param name="Recipient">Who to hand the parcel to. Required.</param>
/// <param name="Line1">Street address. Required.</param>
/// <param name="Line2">Apartment, unit, care-of. Optional.</param>
/// <param name="City">Required.</param>
/// <param name="Region">State, province or county. Optional, because most of the world does not use one.</param>
/// <param name="PostalCode">Required.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2. Upper-cased and trimmed before validation, so "us" is accepted.</param>
public sealed record CheckoutAddressRequest(
    string? Recipient,
    string? Line1,
    string? Line2,
    string? City,
    string? Region,
    string? PostalCode,
    string? CountryCode);
