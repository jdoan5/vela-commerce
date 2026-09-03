namespace VelaCommerce.Api.Contracts;

/// <summary>
/// Body of <c>POST /api/cart/items</c>.
/// <para>
/// <strong>There is no price field here, and there must never be one.</strong> The request names
/// what the shopper wants and how many; the server looks the variant up and charges the catalog's
/// current price. A client that can name its own price is the oldest bug in e-commerce, and it is
/// not prevented by validating the number that arrives — a hostile client sends a plausible one.
/// It is prevented by never having somewhere to put it. The same applies to SKU, display name and
/// currency: all three are read from the catalog row, so a renamed product cannot be smuggled into
/// an order by way of a cart line.
/// </para>
/// </summary>
/// <param name="VariantId">The buyable SKU to add. Unknown or withdrawn variants are answered with 404.</param>
/// <param name="Quantity">
/// Units to add. Merged into any existing line for the same variant, so this is an increment and
/// not an assignment — use PATCH to set an absolute quantity. Must be positive, and the resulting
/// line total must stay within the domain's per-line cap of 99.
/// </param>
public sealed record CartAddItemRequest(Guid VariantId, int Quantity);
