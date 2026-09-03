using System.Text.Json.Serialization;

namespace VelaCommerce.Storefront.Cart;

/// <summary>
/// The cart, exactly as <c>GET /api/cart</c> and every cart mutation answer it.
/// <para>
/// Every write endpoint returns the whole cart rather than the line it touched, which is why this
/// one type is the return value of all five calls and why no mutation is ever followed by a read.
/// The server is the authority on quantity, price and currency; this record is what that authority
/// says, and <c>CartState</c> reconciles its own view to it rather than merging.
/// </para>
/// <para>
/// The response carries more than is read here — a computed subtotal, a total quantity, roll-up
/// flags. They are deliberately ignored: unknown properties are skipped by the deserialiser, and
/// re-deriving the subtotal from the lines keeps one formatter and one arithmetic path in the
/// client instead of two numbers that can disagree.
/// </para>
/// </summary>
public sealed record CartDocument
{
    /// <summary>The cart's single currency, fixed by the first item added to it.</summary>
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";

    /// <summary>Lines in the order they were first added. Null on a malformed response, treated as empty.</summary>
    [JsonPropertyName("lines")] public List<CartLineDocument>? Lines { get; init; }
}

/// <summary>
/// One line of the server's cart.
/// <para>
/// Two prices, and the difference between them is the honest bit of the whole feature.
/// <see cref="UnitPrice"/> is what was captured when the line was added and is what the line total
/// is computed from; <see cref="CurrentUnitPrice"/> is what the catalog charges now, or
/// <see langword="null"/> when the variant has been withdrawn entirely. The cart never silently
/// reprices, so where the two disagree the storefront has to say so.
/// </para>
/// </summary>
public sealed record CartLineDocument
{
    /// <summary>The server's identity for this line. The only key PATCH and DELETE accept.</summary>
    [JsonPropertyName("variantId")] public Guid VariantId { get; init; }

    /// <summary>The SKU, which is the identity the storefront's own components key on.</summary>
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";

    /// <summary>Product and variant names as they read when the line was added, joined by the server.</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>The captured price. Never recomputed by the client.</summary>
    [JsonPropertyName("unitPrice")] public MoneyDocument? UnitPrice { get; init; }

    /// <summary>Units on this line. The server caps this at 99, and so does the client, in that order of authority.</summary>
    [JsonPropertyName("quantity")] public int Quantity { get; init; }

    /// <summary>
    /// The catalog's live price, or null when the variant is no longer sellable. Null is not
    /// "unchanged": it means there is nothing left to compare against and this line will not
    /// check out as it stands.
    /// </summary>
    [JsonPropertyName("currentUnitPrice")] public MoneyDocument? CurrentUnitPrice { get; init; }
}

/// <summary>
/// Money on the wire: a count of minor units and a currency code, never a decimal.
/// <para>
/// The server also sends a preformatted <c>display</c> string. It is not read. The storefront
/// formats money in exactly one place — <c>MoneyFormatter</c> — and a second source of formatted
/// prices is how a cart line and a product page end up disagreeing about a comma.
/// </para>
/// </summary>
public sealed record MoneyDocument
{
    [JsonPropertyName("amount")] public long Amount { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";
}

/// <summary>
/// The slice of <c>GET /api/catalog/products/{slug}</c> the cart needs, and nothing else.
/// <para>
/// This call exists for one reason: the catalog snapshot the storefront browses from carries SKUs
/// but no variant ids, because ids are database keys and the snapshot is a static file generated
/// from the seed. The cart API addresses lines by variant id. So the first time a SKU is added, its
/// id has to be looked up. Every later reference is answered from a cache, and every cart response
/// refreshes that cache for free, since its lines carry both the id and the SKU.
/// </para>
/// </summary>
public sealed record ProductVariantsDocument
{
    [JsonPropertyName("variants")] public List<ProductVariantDocument>? Variants { get; init; }
}

/// <summary>One catalog variant, reduced to the identity mapping the cart needs.</summary>
public sealed record ProductVariantDocument
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";
}

/// <summary>
/// Body of <c>POST /api/cart/items</c>.
/// <para>
/// There is no price here, and there must never be one: the server reads the price from the catalog
/// row. A client that could name its own price would be the oldest bug in e-commerce, and the
/// defence is that there is nowhere to put one.
/// </para>
/// </summary>
/// <param name="VariantId">The SKU to add, by the server's id for it.</param>
/// <param name="Quantity">How many to add. An increment, merged into any existing line.</param>
public sealed record AddCartItemBody(Guid VariantId, int Quantity);

/// <summary>
/// Body of <c>PATCH /api/cart/items/{variantId}</c>. Absolute, not a delta, which is what makes the
/// call safe to retry after a dropped response.
/// </summary>
/// <param name="Quantity">The line's new quantity. Zero removes the line.</param>
public sealed record ChangeQuantityBody(int Quantity);

/// <summary>
/// The subset of RFC 9457 problem details worth showing a shopper. The API answers every refusal in
/// this shape, and the domain's own wording — "Quantity is capped at 99 per line on the demo" — is
/// better than anything the client could re-derive, so it is passed through rather than replaced.
/// </summary>
public sealed record ApiProblem
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }
}

/// <summary>
/// Source-generated readers and writers for everything the cart puts on the wire.
/// <para>
/// Same reason as the catalog's context: reflection-based serialisation drags the reflection stack
/// into the WebAssembly download and produces trim warnings on a Release publish, which this
/// repository builds with warnings as errors.
/// </para>
/// </summary>
/// <remarks>
/// The camel-case policy is for the two request bodies, which have no explicit names: it makes what
/// this client sends read the same as what the API's OpenAPI document says it accepts. Responses do
/// not depend on it — every property above names itself — but case-insensitive matching stays on so
/// a casing change on the wire could never silently null out a price.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CartDocument))]
[JsonSerializable(typeof(ProductVariantsDocument))]
[JsonSerializable(typeof(AddCartItemBody))]
[JsonSerializable(typeof(ChangeQuantityBody))]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class CartApiJsonContext : JsonSerializerContext;
