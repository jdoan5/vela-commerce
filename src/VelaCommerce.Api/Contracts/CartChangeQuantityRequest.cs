namespace VelaCommerce.Api.Contracts;

/// <summary>
/// Body of <c>PATCH /api/cart/items/{variantId}</c>.
/// <para>
/// The quantity is absolute, not a delta. A stepper control that sends "3" three times must end
/// at three, because a dropped response and a retry are ordinary events on a phone; an increment
/// would turn each retry into a silent extra unit. Carrying only the field being changed is also
/// what keeps this a PATCH rather than a PUT of a line the client would otherwise have to send
/// back in full, price included — and the price is exactly the field a client must never send.
/// </para>
/// </summary>
/// <param name="Quantity">
/// The line's new quantity. Zero removes the line, matching <c>Cart.ChangeQuantity</c> in the
/// domain. Negative values are rejected with 400 rather than treated as a removal, because a
/// negative quantity is far more likely to be a bug in the caller than a deliberate request.
/// </param>
public sealed record CartChangeQuantityRequest(int Quantity);
