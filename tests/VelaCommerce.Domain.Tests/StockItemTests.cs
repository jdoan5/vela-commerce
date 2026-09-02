using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// Stock is the one place where "the customer is wrong" is a normal outcome rather than a
/// bug, so the tests here separate the two failure modes deliberately: running out of stock
/// is an expected answer (false, surfaced as a 409), while releasing or shipping units that
/// were never reserved is a broken caller (an exception, surfaced as a fault).
/// </summary>
public sealed class StockItemTests
{
    private static StockItem StockOf(int onHand) => new(Guid.CreateVersion7(), onHand);

    [Fact]
    public void Available_is_on_hand_minus_reserved()
    {
        var stock = StockOf(10);

        stock.TryReserve(4);

        Assert.Equal(10, stock.OnHand);
        Assert.Equal(4, stock.Reserved);
        Assert.Equal(6, stock.Available);
    }

    [Fact]
    public void A_freshly_stocked_item_has_everything_available_and_nothing_reserved()
    {
        var stock = StockOf(7);

        Assert.Equal(7, stock.OnHand);
        Assert.Equal(0, stock.Reserved);
        Assert.Equal(7, stock.Available);
    }

    [Fact]
    public void Stock_cannot_start_out_negative()
    {
        Assert.Throws<DomainException>(() => { _ = new StockItem(Guid.CreateVersion7(), -1); });
    }

    [Fact]
    public void Reserving_within_available_stock_succeeds()
    {
        var stock = StockOf(5);

        var reserved = stock.TryReserve(3);

        Assert.True(reserved);
        Assert.Equal(3, stock.Reserved);
    }

    [Fact]
    public void Reserving_more_than_is_available_returns_false_rather_than_throwing()
    {
        var stock = StockOf(2);

        var reserved = stock.TryReserve(3);

        Assert.False(reserved);
        Assert.Equal(0, stock.Reserved);
        Assert.Equal(2, stock.Available);
    }

    [Fact]
    public void The_last_unit_can_be_reserved_once_and_the_next_shopper_is_turned_away()
    {
        var stock = StockOf(1);

        var first = stock.TryReserve(1);
        var second = stock.TryReserve(1);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, stock.Reserved);
        Assert.Equal(0, stock.Available);
    }

    [Fact]
    public void Reserving_leaves_on_hand_untouched_because_the_unit_is_still_in_the_warehouse()
    {
        var stock = StockOf(4);

        stock.TryReserve(4);

        Assert.Equal(4, stock.OnHand);
        Assert.Equal(0, stock.Available);
    }

    [Fact]
    public void Reservations_accumulate_until_availability_runs_out()
    {
        var stock = StockOf(3);

        Assert.True(stock.TryReserve(2));
        Assert.True(stock.TryReserve(1));
        Assert.False(stock.TryReserve(1));
        Assert.Equal(3, stock.Reserved);
    }

    [Fact]
    public void Releasing_a_reservation_returns_the_units_to_availability()
    {
        var stock = StockOf(5);
        stock.TryReserve(5);

        stock.Release(2);

        Assert.Equal(3, stock.Reserved);
        Assert.Equal(2, stock.Available);
        Assert.Equal(5, stock.OnHand);
    }

    [Fact]
    public void Releasing_more_than_is_reserved_throws_because_it_would_invent_stock()
    {
        var stock = StockOf(5);
        stock.TryReserve(2);

        Assert.Throws<DomainException>(() => stock.Release(3));
    }

    [Fact]
    public void Shipping_removes_the_units_from_both_reserved_and_on_hand()
    {
        var stock = StockOf(5);
        stock.TryReserve(3);

        stock.Ship(3);

        Assert.Equal(2, stock.OnHand);
        Assert.Equal(0, stock.Reserved);
        Assert.Equal(2, stock.Available);
    }

    [Fact]
    public void Shipping_more_than_is_reserved_throws()
    {
        var stock = StockOf(5);
        stock.TryReserve(1);

        Assert.Throws<DomainException>(() => stock.Ship(2));
    }

    [Fact]
    public void Shipping_units_that_were_never_reserved_throws_even_when_they_are_on_hand()
    {
        var stock = StockOf(5);

        Assert.Throws<DomainException>(() => stock.Ship(1));
    }

    [Fact]
    public void Restocking_raises_both_on_hand_and_availability()
    {
        var stock = StockOf(1);
        stock.TryReserve(1);

        stock.Restock(4);

        Assert.Equal(5, stock.OnHand);
        Assert.Equal(1, stock.Reserved);
        Assert.Equal(4, stock.Available);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserving_a_non_positive_quantity_is_a_caller_error_and_throws(int quantity)
    {
        var stock = StockOf(5);

        Assert.Throws<DomainException>(() => { _ = stock.TryReserve(quantity); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Releasing_a_non_positive_quantity_throws(int quantity)
    {
        var stock = StockOf(5);
        stock.TryReserve(2);

        Assert.Throws<DomainException>(() => stock.Release(quantity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Shipping_a_non_positive_quantity_throws(int quantity)
    {
        var stock = StockOf(5);
        stock.TryReserve(2);

        Assert.Throws<DomainException>(() => stock.Ship(quantity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Restocking_a_non_positive_quantity_throws(int quantity)
    {
        var stock = StockOf(5);

        Assert.Throws<DomainException>(() => stock.Restock(quantity));
    }
}
