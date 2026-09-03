using System.Text.Json.Serialization;

using VelaCommerce.Storefront.Cart;
using VelaCommerce.Storefront.Catalog;

namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// An order, exactly as <c>POST /api/checkout</c> and <c>GET /api/orders/{orderNumber}</c> both
/// answer it.
/// <para>
/// One record for both endpoints because the API deliberately returns one shape from both: the
/// confirmation screen and the receipt screen are the same screen fed by the same fields, and a
/// storefront that modelled them separately would be the thing that made them diverge.
/// </para>
/// <para>
/// <see cref="Payment"/> is the exception, and the reason it is nullable rather than required. The
/// gateway's answer is reported once, on the response to the checkout that asked for it, and is
/// persisted nowhere — so a later GET leaves it null. The durable facts a receipt needs
/// (<see cref="Status"/>, <see cref="PaidAt"/>, <see cref="Captured"/>) are on the order itself.
/// </para>
/// </summary>
public sealed record OrderDocument
{
    /// <summary>The human-facing reference, in the shape <c>VELA-XXXXXXX</c>.</summary>
    [JsonPropertyName("orderNumber")] public string OrderNumber { get; init; } = "";

    /// <summary>Pending, Paid, Packed, Shipped or Cancelled, as the order state machine has it.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    /// <summary>When the checkout was accepted. The first stamp on the timeline.</summary>
    [JsonPropertyName("placedAt")] public DateTimeOffset PlacedAt { get; init; }

    /// <summary>When settlement completed, or null while the order is still Pending.</summary>
    [JsonPropertyName("paidAt")] public DateTimeOffset? PaidAt { get; init; }

    /// <summary>The order's currency. Every amount below is in it.</summary>
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";

    /// <summary>Line snapshots taken at checkout, not a live join. Null on a malformed body, treated as empty.</summary>
    [JsonPropertyName("lines")] public List<OrderLineDocument>? Lines { get; init; }

    /// <summary>Sum of the line totals at the revalidated prices.</summary>
    [JsonPropertyName("subtotal")] public MoneyDocument? Subtotal { get; init; }

    /// <summary>Delivery charge, as the server worked it out.</summary>
    [JsonPropertyName("shipping")] public MoneyDocument? Shipping { get; init; }

    /// <summary>Sales tax on the goods, as the server worked it out.</summary>
    [JsonPropertyName("tax")] public MoneyDocument? Tax { get; init; }

    /// <summary>Subtotal plus shipping plus tax. The exact figure the gateway was asked for.</summary>
    [JsonPropertyName("total")] public MoneyDocument? Total { get; init; }

    /// <summary>What the gateway actually took. Zero until settlement, which is why it is shown beside the total rather than instead of it.</summary>
    [JsonPropertyName("captured")] public MoneyDocument? Captured { get; init; }

    /// <summary>Running total of refunds.</summary>
    [JsonPropertyName("refunded")] public MoneyDocument? Refunded { get; init; }

    /// <summary>The address as it was frozen at checkout.</summary>
    [JsonPropertyName("shippingAddress")] public OrderAddressDocument? ShippingAddress { get; init; }

    /// <summary>
    /// A signed capability for this one order, minted fresh on every response — so two reads return
    /// two different strings and both work. It is what makes the receipt link survive a cleared
    /// cookie, and it is why the link must be treated as a secret rather than as an order number.
    /// </summary>
    [JsonPropertyName("retrievalToken")] public string RetrievalToken { get; init; } = "";

    /// <summary>
    /// The token already assembled into the <em>API</em> link that opens this order without a
    /// session. Not the link a shopper bookmarks — that is the storefront page — but it is the one
    /// to quote when demonstrating the endpoint.
    /// </summary>
    [JsonPropertyName("retrievalPath")] public string RetrievalPath { get; init; } = "";

    /// <summary>What the gateway said, present only on the checkout response that asked it.</summary>
    [JsonPropertyName("payment")] public OrderPaymentDocument? Payment { get; init; }

    /// <summary>Units across every line, summed here rather than trusting the server's roll-up, so one arithmetic path serves the whole storefront.</summary>
    public int TotalQuantity
    {
        get
        {
            var total = 0;
            foreach (var line in Lines ?? [])
            {
                total += line.Quantity;
            }

            return total;
        }
    }
}

/// <summary>
/// One line of a placed order.
/// <para>
/// SKU, name and price are the values captured at checkout, so this renders identically a year
/// later whatever has happened to the catalog since. There is deliberately no slug here and the
/// storefront does not go looking for one: linking a receipt back into a live catalog is how a
/// receipt starts telling a different story than the order it describes.
/// </para>
/// </summary>
public sealed record OrderLineDocument
{
    /// <summary>The variant bought. It may no longer resolve to anything sellable.</summary>
    [JsonPropertyName("variantId")] public Guid VariantId { get; init; }

    /// <summary>SKU as it read at checkout.</summary>
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";

    /// <summary>Product and variant name as they read at checkout, joined by the server.</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>The price actually charged, after revalidation against the live catalog.</summary>
    [JsonPropertyName("unitPrice")] public MoneyDocument? UnitPrice { get; init; }

    /// <summary>Units on this line.</summary>
    [JsonPropertyName("quantity")] public int Quantity { get; init; }

    /// <summary>
    /// Unit price times quantity, derived here rather than read from the response's own
    /// <c>lineTotal</c> for the same reason the cart re-derives its subtotal: one arithmetic path in
    /// the client cannot disagree with itself.
    /// </summary>
    public CatalogMoney? LineTotal =>
        UnitPrice is null ? null : new CatalogMoney(UnitPrice.Amount * Quantity, UnitPrice.Currency);
}

/// <summary>The shipping address as stored on the order.</summary>
public sealed record OrderAddressDocument
{
    [JsonPropertyName("recipient")] public string Recipient { get; init; } = "";
    [JsonPropertyName("line1")] public string Line1 { get; init; } = "";
    [JsonPropertyName("line2")] public string? Line2 { get; init; }
    [JsonPropertyName("city")] public string City { get; init; } = "";
    [JsonPropertyName("region")] public string? Region { get; init; }
    [JsonPropertyName("postalCode")] public string PostalCode { get; init; } = "";
    [JsonPropertyName("countryCode")] public string CountryCode { get; init; } = "";
}

/// <summary>
/// What the payment gateway answered.
/// <para>
/// <see cref="Captured"/> and <see cref="AwaitsSettlement"/> are both sent rather than left to be
/// derived from <see cref="Outcome"/>, because the distinction between "the money moved" and "the
/// gateway accepted and will settle later" is the one a confirmation page most often gets wrong. A
/// screen that treats every non-failure as paid shows a receipt for funds that do not exist yet,
/// which is exactly what the <c>Delay</c> scenario exists to catch.
/// </para>
/// </summary>
public sealed record OrderPaymentDocument
{
    /// <summary>Succeeded, Declined, Abandoned or PendingSettlement.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";

    /// <summary>The gateway's own identifier for the attempt. Stable across retries of one idempotency key, and the string to quote in a support ticket.</summary>
    [JsonPropertyName("gatewayReference")] public string GatewayReference { get; init; } = "";

    /// <summary>Populated when and only when the outcome is Declined.</summary>
    [JsonPropertyName("declineReason")] public string? DeclineReason { get; init; }

    /// <summary>True when the money has moved and the order is Paid.</summary>
    [JsonPropertyName("captured")] public bool Captured { get; init; }

    /// <summary>True when the order stays Pending until a signed webhook arrives.</summary>
    [JsonPropertyName("awaitsSettlement")] public bool AwaitsSettlement { get; init; }
}

/// <summary>
/// A refusal from the checkout endpoint: RFC 9457 problem details, plus the four extension members
/// this API adds to them.
/// <para>
/// Extension members sit at the <em>top level</em> of a problem document rather than in a nested
/// bag, which is why <see cref="PriceChanges"/>, <see cref="Shortfall"/>, <see cref="Payment"/> and
/// <see cref="OrderNumber"/> are plain properties here. They are what turn a 409 from "something
/// changed" into "Bosun's Whistle 10 m went from $42.00 to $47.50", which is the difference between
/// an error screen a shopper can act on and one they can only close.
/// </para>
/// </summary>
public sealed record CheckoutProblem
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }

    /// <summary>Every line whose price moved, on the 409 that refuses to reprice silently.</summary>
    [JsonPropertyName("priceChanges")] public List<CheckoutPriceChangeDocument>? PriceChanges { get; init; }

    /// <summary>
    /// The one line that lost the race for the last unit, on the 409 that refuses to oversell.
    /// Exactly one, never a list: the reservation loop stops at the first refusal and rolls back, so
    /// naming the others would mean guessing.
    /// </summary>
    [JsonPropertyName("shortfall")] public CheckoutShortfallDocument? Shortfall { get; init; }

    /// <summary>The gateway's answer, on the 402 that reports a decline or an abandonment.</summary>
    [JsonPropertyName("payment")] public OrderPaymentDocument? Payment { get; init; }

    /// <summary>
    /// The order that exists despite the failure. Present on a 402 and on the 502 that means the
    /// gateway could not be reached — in both cases an order row was created, and hiding it would
    /// leave the shopper with reserved stock and no way to look at it.
    /// </summary>
    [JsonPropertyName("orderNumber")] public string? OrderNumber { get; init; }
}

/// <summary>
/// One line whose price moved between the cart and the checkout, or which left the catalog
/// altogether.
/// </summary>
public sealed record CheckoutPriceChangeDocument
{
    [JsonPropertyName("variantId")] public Guid VariantId { get; init; }
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>The price the line was added at, and the one the shopper has been shown all along.</summary>
    [JsonPropertyName("was")] public MoneyDocument? Was { get; init; }

    /// <summary>The catalog's live price, or null when the variant is no longer sold.</summary>
    [JsonPropertyName("now")] public MoneyDocument? Now { get; init; }

    /// <summary>Now minus was, signed. Positive means the shopper was holding a bargain that has expired.</summary>
    [JsonPropertyName("difference")] public MoneyDocument? Difference { get; init; }

    /// <summary>True when the variant left the catalog rather than merely changed price. Sent by the server so the two cases can be worded differently without comparing nulls.</summary>
    [JsonPropertyName("noLongerSold")] public bool NoLongerSold { get; init; }
}

/// <summary>The line that could not be reserved, and how short it fell.</summary>
public sealed record CheckoutShortfallDocument
{
    [JsonPropertyName("variantId")] public Guid VariantId { get; init; }
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>Units the cart asked for.</summary>
    [JsonPropertyName("requested")] public int Requested { get; init; }

    /// <summary>
    /// Units free when the shortfall was discovered, or null when the variant has no stock record at
    /// all. <strong>Advisory only</strong>, and the UI says so: it is read after the failed
    /// reservation, so another shopper may have taken more since. The number that decided the
    /// outcome lived inside a conditional UPDATE and never entered a process.
    /// </summary>
    [JsonPropertyName("available")] public int? Available { get; init; }
}

/// <summary>
/// Body of <c>POST /api/checkout</c>.
/// <para>
/// What is absent is the design, and the storefront has to respect it rather than work around it:
/// no cart id, no lines, no prices, no total, no card number. The server reads the cart its own
/// session cookie names, revalidates every price against the live catalog and computes the total
/// itself. There is nowhere in this record to put a price, which is what makes "the client cannot
/// check out at a price it invented" a property of the type rather than a promise.
/// </para>
/// </summary>
/// <param name="ShippingAddress">Where the order goes. Validated by the domain, not by this record.</param>
/// <param name="IdempotencyKey">
/// This attempt's key. Sent here <em>as well as</em> in the <c>Idempotency-Key</c> header: the API
/// accepts either and permits both when they agree, and duplicating it means a proxy that strips
/// unrecognised request headers turns into a redundant field rather than a 400 on every checkout.
/// </param>
/// <param name="PaymentScenario">
/// Which path to make the payment simulator take. Null lets the simulator fall back to the trailing
/// cents of the order total; the storefront always names one, because a reviewer choosing "Card
/// declined" from a list should not have to also arrange for their basket to total $47.01.
/// </param>
public sealed record PlaceOrderBody(
    CheckoutAddressBody ShippingAddress,
    string IdempotencyKey,
    string? PaymentScenario);

/// <summary>A postal address on its way to <c>ShippingAddress.Validate()</c>.</summary>
public sealed record CheckoutAddressBody(
    string Recipient,
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string PostalCode,
    string CountryCode);

/// <summary>
/// Source-generated readers and writers for everything checkout puts on the wire.
/// <para>
/// Same reason as the catalog's and the cart's contexts: reflection-based serialisation drags the
/// reflection stack into the WebAssembly download and produces trim warnings on a Release publish,
/// which this repository builds with warnings as errors.
/// </para>
/// </summary>
/// <remarks>
/// The camel-case policy is for <see cref="PlaceOrderBody"/> and <see cref="CheckoutAddressBody"/>,
/// which have no explicit names, so what this client sends reads the same as what the OpenAPI
/// document says the endpoint accepts. Responses do not depend on it — every property above names
/// itself — but case-insensitive matching stays on so a casing change on the wire could never
/// silently null out a total.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OrderDocument))]
[JsonSerializable(typeof(CheckoutProblem))]
[JsonSerializable(typeof(PlaceOrderBody))]
internal sealed partial class CheckoutApiJsonContext : JsonSerializerContext;
