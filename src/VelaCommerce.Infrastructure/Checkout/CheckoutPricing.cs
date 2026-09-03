using VelaCommerce.Domain.Common;

namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// What a cart costs once shipping and tax are added: the three numbers
/// <c>Order.FromCart</c> needs, computed together so they cannot be sourced from different rules.
/// </summary>
/// <param name="Subtotal">Sum of the line totals, at the prices the checkout revalidated.</param>
/// <param name="Shipping">Delivery charge, in the cart's currency.</param>
/// <param name="Tax">Sales tax, in the cart's currency.</param>
public sealed record CheckoutQuote(Money Subtotal, Money Shipping, Money Tax)
{
    /// <summary>
    /// What the gateway will be asked to take. Derived rather than stored, so it can never
    /// disagree with the three parts it is made of — and it is the same expression
    /// <c>Order.Total</c> uses, which is what lets <c>Order.MarkPaid</c> insist the captured
    /// amount matches to the cent.
    /// </summary>
    public Money Total => Subtotal + Shipping + Tax;
}

/// <summary>
/// The demo's shipping and tax rules.
/// <para>
/// <strong>These numbers are illustrative and deliberately trivial.</strong> A real store needs a
/// rate table per destination and a tax engine that knows about nexus, exemptions and reduced
/// rates; inventing one here would be a large pile of code that demonstrates nothing about the
/// parts of this system worth reading. What this type does demonstrate is the property that
/// actually matters and is easy to get wrong: <strong>every amount stays in integer minor
/// units</strong>, and the one place a fraction appears — applying a percentage — rounds
/// explicitly and half-to-even, the same way <see cref="Money.FromDecimal"/> does.
/// </para>
/// <para>
/// Pure and static on purpose: no clock, no database, no configuration. A quote for a given
/// subtotal is the same on every machine and in every test run, so an order total is reproducible
/// and the payment simulator's amount-driven scenarios stay deterministic.
/// </para>
/// </summary>
public static class CheckoutPricing
{
    /// <summary>Flat delivery charge, in minor units of the cart's currency ($9.95).</summary>
    public const long FlatShippingMinorUnits = 995;

    /// <summary>Subtotal at or above which delivery is free, in minor units ($150.00).</summary>
    public const long FreeShippingThresholdMinorUnits = 15_000;

    /// <summary>
    /// Sales tax in basis points (875 = 8.75%). Basis points rather than a <c>decimal</c> so the
    /// rate itself is an integer and the only rounding in the whole calculation is the single,
    /// explicit one in <see cref="ApplyBasisPoints"/>.
    /// </summary>
    public const int TaxBasisPoints = 875;

    /// <summary>
    /// Prices a cart subtotal.
    /// <para>
    /// Tax is charged on goods only, not on delivery. Some jurisdictions do tax shipping and some
    /// do not; picking one and saying so is more honest than picking one silently, and the choice
    /// is isolated here so a real rule can replace it without touching the checkout handler.
    /// </para>
    /// <para>
    /// The same currency is used throughout: the domain refuses to mix currencies within a cart,
    /// and the flat amounts above are applied as figures in whatever currency the cart is in
    /// rather than converted. That is a simplification a multi-currency store could not make, and
    /// it is the second thing a real pricing service would replace.
    /// </para>
    /// </summary>
    /// <param name="subtotal">The cart subtotal. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">The subtotal is negative.</exception>
    public static CheckoutQuote Quote(Money subtotal)
    {
        if (subtotal.IsNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subtotal),
                subtotal,
                "A cart subtotal cannot be negative; a negative line price would have been "
                + "refused when the line was created.");
        }

        var shipping = subtotal.Amount >= FreeShippingThresholdMinorUnits
            ? Money.Zero(subtotal.Currency)
            : new Money(FlatShippingMinorUnits, subtotal.Currency);

        var tax = new Money(ApplyBasisPoints(subtotal.Amount, TaxBasisPoints), subtotal.Currency);

        return new CheckoutQuote(subtotal, shipping, tax);
    }

    /// <summary>
    /// Multiplies a minor-unit amount by a rate in basis points, rounding half to even.
    /// <para>
    /// Integer arithmetic end to end. The obvious alternative — convert to <c>decimal</c>,
    /// multiply, round back — works, but it puts a second numeric representation in the middle of
    /// the one calculation where the representation is the point. Half-to-even matches
    /// <see cref="Money.FromDecimal"/>, so a tax figure computed here and one computed from a
    /// major-unit decimal elsewhere cannot land a cent apart.
    /// </para>
    /// <para>
    /// <c>amount</c> is non-negative (the caller has already checked), so the "round half away
    /// from zero" ambiguity that plagues signed integer division never arises.
    /// </para>
    /// </summary>
    private static long ApplyBasisPoints(long amount, int basisPoints)
    {
        const long Scale = 10_000L;

        var scaled = amount * basisPoints;
        var quotient = Math.DivRem(scaled, Scale, out var remainder);

        // Compare 2 x remainder against the divisor rather than remainder against half of it, so
        // an odd divisor would not silently round the halfway case in one direction.
        var doubled = remainder * 2;

        if (doubled > Scale || (doubled == Scale && (quotient & 1L) == 1L))
        {
            quotient++;
        }

        return quotient;
    }
}
