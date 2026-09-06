using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
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

    // ---------------------------------------------------------------------------------------
    // A SECOND ROUND, AND THE REASON IT HAPPENED IS WORTH MORE THAN THE TESTS.
    //
    // Stryker's score moved from 71.61% to 73.55% across a commit that added a static array to
    // OrderStateMachine and changed nothing else in the domain and nothing at all in this project.
    // Six mutants in six other files flipped from Survived to Killed. Not one of them was killed by
    // a test, because no test had changed: applying the first of them by hand — `quantity <= 0`
    // widened to `quantity < 0` in Cart.AddItem — left all 202 domain tests green. The tool had
    // reported a kill for a change nothing can detect.
    //
    // So the improvement was an artefact of Stryker's per-test coverage selection shifting when the
    // assembly's layout changed, and the honest reading is that the LOWER number was the better
    // one. Chasing the six down found three real gaps — a reservation for zero units, a product's
    // description and a variant's name, each verified by breaking the code and watching the new
    // test go red. The other three cannot be killed by any single-mutant tool, and the note at the
    // bottom of this file argues that rather than papering over it.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A cart line of nothing is refused. The behaviour is worth pinning and was untested; what it
    /// does NOT do is kill the mutant that started this, and finding that out is the useful part.
    /// <para>
    /// Widen <c>Cart.AddItem</c>'s <c>quantity &lt;= 0</c> to <c>&lt; 0</c> and this test still
    /// passes, because zero then falls through to <c>new CartLine(...)</c>, whose own
    /// <c>AssertQuantityInRange</c> throws the same <see cref="DomainException"/>. Break CartLine's
    /// guard instead and Cart's catches it first. <b>The two shield each other</b>, so neither
    /// mutant is killable alone and no single-mutant tool can ever report them dead honestly. That
    /// is redundant validation working exactly as intended, not a gap — see the note at the bottom
    /// of this file.
    /// </para>
    /// </summary>
    [Fact]
    public void Adding_nothing_to_a_cart_is_refused()
    {
        var cart = new Cart(Guid.CreateVersion7());

        Assert.Throws<DomainException>(() =>
            cart.AddItem(Guid.CreateVersion7(), "VELA-TOTE-01", "Harbour Tote", Usd(4_500), 0));

        Assert.True(cart.IsEmpty);
    }

    /// <summary>
    /// Reserving nothing is refused at the reservation as well as at the ledger. The
    /// <see cref="StockItem"/> half of this pair has been tested since the first mutation round;
    /// the row that records the hold had no equivalent, so a reservation for zero units could be
    /// written even though no ledger would move for it.
    /// </summary>
    [Fact]
    public void A_reservation_for_no_units_is_refused()
    {
        Assert.Throws<DomainException>(() =>
            new StockReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, DateTimeOffset.UnixEpoch));
    }

    /// <summary>
    /// A product keeps the description it was given. The surviving mutant deleted the left half of
    /// <c>description?.Trim() ?? string.Empty</c>, so every product in the catalog would have had an
    /// empty description — 288 blank product pages, with nothing failing and nothing logged.
    /// </summary>
    [Fact]
    public void A_product_keeps_its_description_and_category()
    {
        var product = new Product("storm-jib", "Storm Jib", "  Heavy weather headsail.  ", "  sails  ");

        Assert.Equal("Heavy weather headsail.", product.Description);
        Assert.Equal("sails", product.Category);
    }

    /// <summary>
    /// The same deletion one level down, on a variant's name.
    /// <para>
    /// Written first to assert both halves — a named variant keeps its name, an unnamed one falls
    /// back to empty — and the second half does not compile. <c>AddVariant</c> declares
    /// <c>string variantName</c>, not <c>string?</c>, so passing null is CS8625 under
    /// <c>-warnaserror</c>. The same is true of <c>Product</c>'s description and category.
    /// </para>
    /// <para>
    /// Which means the <c>?? string.Empty</c> fallbacks those constructors carry are unreachable
    /// from any caller the compiler is happy with. They are not useless — EF materialises these
    /// objects without consulting nullable annotations, so the fallback still stands between a null
    /// column and a NullReferenceException — but they cannot be exercised from a test without
    /// either loosening a public signature or suppressing the warning, and neither is worth doing
    /// to reach a branch. So this asserts the half that is reachable, which is also the half that
    /// kills the mutant: replace <c>name?.Trim() ?? string.Empty</c> with <c>string.Empty</c> and
    /// this fails.
    /// </para>
    /// </summary>
    [Fact]
    public void A_variant_keeps_the_name_it_was_given()
    {
        var product = new Product("storm-jib", "Storm Jib", "Heavy weather headsail.", "sails");

        var named = product.AddVariant("VELA-JIB-01", "  Standard  ", Usd(42_000));

        Assert.Equal("Standard", named.Name);
    }

    // ---------------------------------------------------------------------------------------
    // THE THREE THAT ARE LEFT ALIVE ON PURPOSE, AND WHY THAT IS THE RIGHT ANSWER.
    //
    // Of the six mutants Stryker's shifting selection drew attention to, three are genuinely
    // unkillable and should stay that way. All three are the same `quantity <= 0` guard.
    //
    //   Cart.AddItem and CartLine's constructor SHIELD EACH OTHER. Break either alone and zero is
    //   still refused, by the other one, with the same exception type. Only breaking BOTH lets a
    //   zero-quantity line exist, and a tool that changes one thing at a time cannot construct
    //   that. Verified by hand in both directions rather than assumed.
    //
    //   OrderLine's is unreachable from anywhere. It is internal, and Order.FromCart is its only
    //   caller, building it out of cart lines that are already at least one.
    //
    // The only way to "kill" any of the three is to make a constructor public or reach it by
    // reflection — testing an arrangement the application does not have, so that a number goes up.
    // Redundant validation is a thing worth having on the path where money and stock meet, and a
    // mutation score that punishes it is measuring the tool's reach, not the code's safety.
    // ---------------------------------------------------------------------------------------
}
