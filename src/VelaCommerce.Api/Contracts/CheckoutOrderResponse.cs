namespace VelaCommerce.Api.Contracts;

/// <summary>
/// A placed order, and the only shape <c>POST /api/checkout</c> and
/// <c>GET /api/orders/{orderNumber}</c> return.
/// <para>
/// One contract for both, so the confirmation page and the receipt page are the same screen fed by
/// the same fields. The order id is deliberately absent — an order is addressed by its human-facing
/// number, and publishing the key would invite a client to send one back, which is the decision the
/// signed retrieval token exists to avoid ever having to make.
/// </para>
/// <para>
/// Every amount is a <see cref="MoneyDto"/>: minor units plus a rendered string, so no client ever
/// divides money by 100 in a language where that produces 19.989999999999998.
/// </para>
/// </summary>
/// <param name="OrderNumber">The reference on the confirmation page, in the shape <c>VELA-XXXXXXX</c>.</param>
/// <param name="Status">Pending, Paid, Packed, Shipped or Cancelled, as the order state machine has it.</param>
/// <param name="PlacedAt">When the checkout was accepted. Not read from the clock by the aggregate — passed in.</param>
/// <param name="PaidAt">When settlement completed, or null while the order is still Pending.</param>
/// <param name="Currency">The order's currency. Every amount below is in it.</param>
/// <param name="Lines">Snapshots taken at checkout, not a live join: an order still reads correctly after the catalog is renamed or withdrawn.</param>
/// <param name="Subtotal">Sum of the line totals at the revalidated prices.</param>
/// <param name="Shipping">Delivery charge.</param>
/// <param name="Tax">Sales tax on the goods.</param>
/// <param name="Total">Subtotal plus shipping plus tax. The exact figure the gateway is asked for.</param>
/// <param name="Captured">What the gateway actually took. Zero until settlement.</param>
/// <param name="Refunded">Running total of refunds, which the database will not let exceed <paramref name="Captured"/>.</param>
/// <param name="ShippingAddress">The address as it was frozen at checkout.</param>
/// <param name="RetrievalToken">
/// A signed capability for this one order. It is minted fresh on every response, so two calls
/// return two different strings that both work — Data Protection ciphertext is randomised, and a
/// stable token would be a stable secret.
/// </param>
/// <param name="RetrievalPath">
/// <paramref name="RetrievalToken"/> already assembled into the link that opens this order without
/// a session. Relative, so it is correct whatever host or scheme the API is reached on.
/// </param>
/// <param name="Payment">
/// What the gateway said, present only on the response to the checkout that asked it. A later GET
/// leaves this null: the durable facts — status, captured amount, paid-at — are on the order
/// itself, and the gateway's answer is not persisted anywhere for a second read to find.
/// </param>
public sealed record CheckoutOrderResponse(
    string OrderNumber,
    string Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? PaidAt,
    string Currency,
    IReadOnlyList<CheckoutOrderLineResponse> Lines,
    MoneyDto Subtotal,
    MoneyDto Shipping,
    MoneyDto Tax,
    MoneyDto Total,
    MoneyDto Captured,
    MoneyDto Refunded,
    CheckoutAddressResponse ShippingAddress,
    string RetrievalToken,
    string RetrievalPath,
    CheckoutPaymentResponse? Payment)
{
    /// <summary>Units across all lines, for a summary line that should not require the client to sum.</summary>
    public int TotalQuantity => Lines.Sum(line => line.Quantity);
}

/// <summary>
/// One line of a placed order. SKU, name and price are the values captured at checkout, so this
/// renders identically a year later whatever has happened to the catalog since.
/// </summary>
/// <param name="VariantId">The variant bought. Kept so a "buy it again" link is possible; it may no longer resolve.</param>
/// <param name="Sku">As it read at checkout.</param>
/// <param name="DisplayName">Product and variant name as they read at checkout.</param>
/// <param name="UnitPrice">The price actually charged, after revalidation against the live catalog.</param>
/// <param name="Quantity">Units on this line.</param>
public sealed record CheckoutOrderLineResponse(
    Guid VariantId,
    string Sku,
    string DisplayName,
    MoneyDto UnitPrice,
    int Quantity)
{
    /// <summary>Unit price times quantity, derived so it cannot drift from its own inputs.</summary>
    public MoneyDto LineTotal => new(UnitPrice.Amount * Quantity, UnitPrice.Currency);
}

/// <summary>The shipping address as stored on the order.</summary>
/// <param name="Recipient">Who the parcel is for.</param>
/// <param name="Line1">Street address.</param>
/// <param name="Line2">Apartment, unit, care-of, or null.</param>
/// <param name="City">City.</param>
/// <param name="Region">State, province or county, or null.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2.</param>
public sealed record CheckoutAddressResponse(
    string Recipient,
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string PostalCode,
    string CountryCode);

/// <summary>
/// What the payment gateway answered, reported once on the checkout response.
/// <para>
/// Both flags are here rather than left to the client to derive from
/// <paramref name="Outcome"/>, because the distinction between "the money moved" and "the gateway
/// accepted and will settle later" is the one a confirmation page most often gets wrong — a UI
/// that treats every non-failure as paid shows a receipt for funds that do not exist yet.
/// </para>
/// </summary>
/// <param name="Outcome">Succeeded, Declined, Abandoned or PendingSettlement.</param>
/// <param name="GatewayReference">The gateway's own identifier for the attempt. Stable across retries of one idempotency key, and the string to quote in a support ticket.</param>
/// <param name="DeclineReason">Populated when and only when the outcome is Declined.</param>
/// <param name="Captured">True when the money has moved and the order is Paid.</param>
/// <param name="AwaitsSettlement">True when the order stays Pending until a signed webhook arrives.</param>
public sealed record CheckoutPaymentResponse(
    string Outcome,
    string GatewayReference,
    string? DeclineReason,
    bool Captured,
    bool AwaitsSettlement);
