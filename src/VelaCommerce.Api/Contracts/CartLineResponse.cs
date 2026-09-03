namespace VelaCommerce.Api.Contracts;

/// <summary>
/// One line of the current visitor's cart, carrying both the price that was captured when the
/// line was added and the price the catalog charges right now.
/// <para>
/// Two prices rather than one, because the alternative designs are both worse. Silently
/// rewriting the line to the live price means the shopper's total changes between the page they
/// looked at and the page they submit, with nothing on screen to explain it. Silently honouring
/// the captured price means the store sells at whatever number happened to be cached in a row
/// that may be weeks old. Showing both and letting the shopper decide is the only version that
/// is honest to everybody, so the difference is surfaced here and the line is left alone.
/// </para>
/// <para>
/// The variant id is the only identifier exposed. A cart line has a database key of its own, but
/// publishing it would invite clients to address lines by it, and the shopper's mental model is
/// "the blue one, size M" — the variant — not a row.
/// </para>
/// </summary>
/// <param name="VariantId">The buyable SKU this line is for; also the key used by PATCH and DELETE.</param>
/// <param name="Sku">Copied onto the line when it was added, so the cart still renders if the catalog moves.</param>
/// <param name="DisplayName">Product and variant name as they read at the moment the line was added.</param>
/// <param name="UnitPrice">The captured price. This is the number the line total is computed from.</param>
/// <param name="Quantity">Units on this line, capped by the domain at 99.</param>
/// <param name="CurrentUnitPrice">
/// The catalog's live price for this variant, or <see langword="null"/> when the variant has been
/// withdrawn since the line was added. Null is meaningfully different from "unchanged": it means
/// there is nothing left to compare against and this line cannot be checked out as it stands.
/// </param>
public sealed record CartLineResponse(
    Guid VariantId,
    string Sku,
    string DisplayName,
    MoneyDto UnitPrice,
    int Quantity,
    MoneyDto? CurrentUnitPrice)
{
    /// <summary>
    /// Captured unit price times quantity. Derived rather than passed in, so it cannot disagree
    /// with the two numbers it is made of — a line total that has drifted from its own inputs is
    /// the sort of bug that is only ever found by a customer.
    /// </summary>
    public MoneyDto LineTotal => new(UnitPrice.Amount * Quantity, UnitPrice.Currency);

    /// <summary>
    /// Live price minus captured price, or <see langword="null"/> when nothing moved (or when
    /// there is no live price to compare against). Signed on purpose: positive means the shopper
    /// is currently holding a bargain, negative means they would pay less by re-adding it, and a
    /// client that only wants to know "did something change" can look at
    /// <see cref="PriceChanged"/> instead of comparing two amounts itself.
    /// </summary>
    public MoneyDto? PriceDifference =>
        CurrentUnitPrice is { } current && current.Amount != UnitPrice.Amount
            ? new MoneyDto(current.Amount - UnitPrice.Amount, UnitPrice.Currency)
            : null;

    /// <summary>
    /// Whether the catalog moved under this line. Computed from
    /// <see cref="PriceDifference"/> rather than alongside it, so the flag and the amount can
    /// never tell the shopper two different stories.
    /// </summary>
    public bool PriceChanged => PriceDifference is not null;

    /// <summary>
    /// Whether the variant is still sellable. False means the SKU was withdrawn after it was
    /// added; the line is kept and shown rather than deleted behind the shopper's back, because a
    /// line that vanishes silently is indistinguishable from a bug in the cart.
    /// </summary>
    public bool StillInCatalog => CurrentUnitPrice is not null;
}
