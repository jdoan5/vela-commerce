using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// The edges the suite was stepping over, found by mutation testing rather than by reading.
/// <para>
/// Stryker ran over the domain and 26 non-cosmetic mutants survived — changes to production code
/// that every one of 387 tests was happy with. They clustered, and the clusters are what this file
/// is: the <c>checked</c> keyword on money arithmetic could be deleted with nothing noticing;
/// <c>quantity &lt;= 0</c> could become <c>&lt; 0</c> in four places because no test ever passed
/// zero; <c>amount.IsNegative || amount.IsZero</c> could become <c>&amp;&amp;</c> for the same
/// reason; and three of the four <see cref="Money"/> comparison operators were never called at all.
/// </para>
/// <para>
/// None of these is exotic. Each is the boundary immediately beside a case the suite already
/// covered, which is the shape of gap that reading tests does not reveal and mutating code does.
/// </para>
/// </summary>
public sealed class BoundaryTests
{
    private static Money Usd(long amount) => new(amount);

    // ---------------------------------------------------------------------------------------
    // checked arithmetic. Deleting `checked` from +, - and * survived: nothing forced an overflow,
    // so the guard was decoration. Money is a long of minor units, and an unchecked overflow does
    // not throw — it silently wraps to a negative, which in a shop is a total that pays the
    // customer.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Adding_past_the_top_of_the_range_throws_rather_than_wrapping()
    {
        var huge = Usd(long.MaxValue);

        Assert.Throws<OverflowException>(() => huge + Usd(1));
    }

    [Fact]
    public void Subtracting_past_the_bottom_of_the_range_throws_rather_than_wrapping()
    {
        var lowest = Usd(long.MinValue);

        Assert.Throws<OverflowException>(() => lowest - Usd(1));
    }

    [Fact]
    public void Multiplying_past_the_top_of_the_range_throws_rather_than_wrapping()
    {
        var half = Usd(long.MaxValue / 2);

        Assert.Throws<OverflowException>(() => half * 3);
    }

    // ---------------------------------------------------------------------------------------
    // The comparison operators. `<` could be widened to `<=` and the bodies of `>`, `<=` and `>=`
    // could be deleted outright, because nothing compared two equal amounts and three of the four
    // operators were never called. Equality is the only interesting input for a comparison
    // operator: every other case is shared with the one beside it.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Comparing_two_equal_amounts_puts_neither_above_the_other()
    {
        var left = Usd(4_500);
        var right = Usd(4_500);

        Assert.False(left < right);
        Assert.False(left > right);
        Assert.True(left <= right);
        Assert.True(left >= right);
    }

    [Fact]
    public void Comparing_two_different_amounts_orders_them_all_four_ways()
    {
        var smaller = Usd(1_000);
        var larger = Usd(1_001);

        Assert.True(smaller < larger);
        Assert.False(smaller > larger);
        Assert.True(smaller <= larger);
        Assert.False(smaller >= larger);
    }

    // ---------------------------------------------------------------------------------------
    // Zero quantities. `quantity <= 0` could become `quantity < 0` in StockItem, StockReservation,
    // Cart, CartLine and OrderLine, because every test passed a positive number or a negative one
    // and none passed zero. A zero-quantity line is not harmless: it is a cart line that renders,
    // reserves nothing, and cannot be reasoned about.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Zero on-hand is a legal state and the mutation is what proves it has to be.
    /// <para>
    /// I wrote this test the other way round first, asserting that a stock item cannot be created
    /// with no units, and it failed — correctly. A variant that has sold out still has a row; the
    /// guard is <c>onHand &lt; 0</c>, and the surviving mutant tightened it to <c>&lt;= 0</c>,
    /// which would make every sold-out product impossible to represent. So zero is the case worth
    /// asserting, and asserting that it is ACCEPTED rather than refused.
    /// </para>
    /// </summary>
    [Fact]
    public void Stock_may_be_created_with_no_units_because_sold_out_is_a_real_state()
    {
        var soldOut = new StockItem(Guid.CreateVersion7(), 0);

        Assert.Equal(0, soldOut.OnHand);
        Assert.Equal(0, soldOut.Available);
        Assert.False(soldOut.TryReserve(1));
    }

    [Fact]
    public void Stock_cannot_be_created_with_a_negative_count()
    {
        Assert.Throws<DomainException>(() => new StockItem(Guid.CreateVersion7(), -1));
    }

    [Fact]
    public void Reserving_nothing_is_refused()
    {
        var stock = new StockItem(Guid.CreateVersion7(), 5);

        Assert.Throws<DomainException>(() => stock.TryReserve(0));

        Assert.Equal(0, stock.Reserved);
    }

    [Fact]
    public void Releasing_nothing_is_refused()
    {
        var stock = new StockItem(Guid.CreateVersion7(), 5);
        Assert.True(stock.TryReserve(3));

        Assert.Throws<DomainException>(() => stock.Release(0));
    }

    /// <summary>
    /// The other side of the same guard, and the one the mutation exposed: <c>quantity &gt;
    /// Reserved</c> could be loosened to <c>&gt;=</c> because no test ever released exactly what
    /// was reserved. That is the ordinary case — a cancelled order gives back all of it — and it
    /// has to succeed, so widening the refusal by one would have broken the common path while
    /// every test stayed green.
    /// </summary>
    [Fact]
    public void Releasing_exactly_what_was_reserved_succeeds_and_empties_the_hold()
    {
        var stock = new StockItem(Guid.CreateVersion7(), 10);
        Assert.True(stock.TryReserve(4));

        stock.Release(4);

        Assert.Equal(0, stock.Reserved);
        Assert.Equal(10, stock.OnHand);
        Assert.Equal(10, stock.Available);
    }

    [Fact]
    public void Releasing_one_more_than_was_reserved_is_refused()
    {
        var stock = new StockItem(Guid.CreateVersion7(), 10);
        Assert.True(stock.TryReserve(4));

        Assert.Throws<DomainException>(() => stock.Release(5));
    }
}
