using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Orders;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// The order aggregate is where money becomes irreversible, so these tests read as the
/// commercial rules rather than as method coverage: an order is a faithful snapshot of the
/// cart, it settles for exactly its total and only once, it moves only along the documented
/// lifecycle, and it can never refund more than it took.
/// </summary>
public sealed class OrderTests
{
    private static readonly DateTimeOffset SettledAt = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private const long ShippingCents = 500;
    private const long TaxCents = 180;

    private static ShippingAddress ValidAddress() => new()
    {
        Recipient = "Ada Lovelace",
        Line1 = "12 Marylebone Road",
        City = "London",
        PostalCode = "NW1 5LA",
        CountryCode = "GB"
    };

    private static Cart CartWith(params (long UnitPriceCents, int Quantity)[] lines)
    {
        var cart = new Cart(Guid.CreateVersion7());
        var index = 0;
        foreach (var (unitPrice, quantity) in lines)
        {
            index++;
            cart.AddItem(Guid.CreateVersion7(), $"VELA-{index:D3}", $"Item {index}", new Money(unitPrice), quantity);
        }

        return cart;
    }

    /// <summary>Subtotal 2000 + shipping 500 + tax 180 = a total of 2680 cents.</summary>
    private static Order PendingOrder() => Order.FromCart(
        CartWith((1_000L, 2)),
        "VC-2001",
        "idempotency-2001",
        ValidAddress(),
        new Money(ShippingCents),
        new Money(TaxCents));

    private static Order PaidOrder()
    {
        var order = PendingOrder();
        order.MarkPaid(order.Total, SettledAt);
        return order;
    }

    [Fact]
    public void An_order_copies_every_cart_line_including_the_name_and_sku_it_was_sold_under()
    {
        var cart = new Cart(Guid.CreateVersion7());
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);

        var order = Order.FromCart(cart, "VC-1001", "idempotency-1001", ValidAddress(), new Money(500), new Money(360));

        var line = Assert.Single(order.Lines);
        Assert.Equal(order.Id, line.OrderId);
        Assert.Equal(variantId, line.VariantId);
        Assert.Equal("VELA-TOTE-01", line.Sku);
        Assert.Equal("Harbour Tote", line.DisplayName);
        Assert.Equal(new Money(4_500), line.UnitPrice);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(new Money(9_000), line.LineTotal);
    }

    [Fact]
    public void An_order_inherits_the_session_and_currency_of_the_cart_it_came_from()
    {
        var cart = CartWith((1_000L, 1));

        var order = Order.FromCart(cart, "VC-1002", "idempotency-1002", ValidAddress(), Money.Zero(), Money.Zero());

        Assert.Equal(cart.DemoSessionId, order.DemoSessionId);
        Assert.Equal(cart.Currency, order.Currency);
    }

    [Fact]
    public void An_empty_cart_cannot_become_an_order()
    {
        var cart = new Cart(Guid.CreateVersion7());

        Assert.Throws<DomainException>(() =>
        {
            _ = Order.FromCart(cart, "VC-1003", "idempotency-1003", ValidAddress(), Money.Zero(), Money.Zero());
        });
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_order_requires_an_idempotency_key_because_that_is_what_defeats_a_double_submit(string? key)
    {
        var cart = CartWith((1_000L, 1));

        Assert.Throws<DomainException>(() =>
        {
            _ = Order.FromCart(cart, "VC-1004", key!, ValidAddress(), Money.Zero(), Money.Zero());
        });
    }

    [Fact]
    public void An_order_cannot_be_placed_against_an_incomplete_shipping_address()
    {
        var cart = CartWith((1_000L, 1));
        var addressWithNoRecipient = ValidAddress() with { Recipient = "  " };

        Assert.Throws<DomainException>(() =>
        {
            _ = Order.FromCart(cart, "VC-1005", "idempotency-1005", addressWithNoRecipient, Money.Zero(), Money.Zero());
        });
    }

    [Fact]
    public void A_new_order_is_pending_with_nothing_captured_and_nothing_refunded()
    {
        var order = PendingOrder();

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.True(order.Captured.IsZero);
        Assert.True(order.Refunded.IsZero);
        Assert.Null(order.PaidAt);
    }

    [Fact]
    public void The_total_is_the_subtotal_plus_shipping_plus_tax()
    {
        var order = Order.FromCart(
            CartWith((1_000L, 2), (2_500L, 1)),
            "VC-1006",
            "idempotency-1006",
            ValidAddress(),
            new Money(ShippingCents),
            new Money(TaxCents));

        Assert.Equal(new Money(4_500), order.Subtotal);
        Assert.Equal(new Money(4_500 + ShippingCents + TaxCents), order.Total);
    }

    [Fact]
    public void Settling_a_pending_order_records_the_capture_and_the_moment_it_happened()
    {
        var order = PendingOrder();

        order.MarkPaid(order.Total, SettledAt);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(new Money(2_680), order.Captured);
        Assert.Equal(SettledAt, order.PaidAt);
    }

    [Theory]
    [InlineData(2_679L)]
    [InlineData(2_681L)]
    [InlineData(0L)]
    public void A_capture_that_does_not_equal_the_total_is_refused(long capturedCents)
    {
        var order = PendingOrder();

        Assert.Throws<DomainException>(() => order.MarkPaid(new Money(capturedCents), SettledAt));
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void A_negative_capture_is_refused()
    {
        var order = PendingOrder();

        Assert.Throws<DomainException>(() => order.MarkPaid(new Money(-2_680), SettledAt));
    }

    [Fact]
    public void Settling_the_same_order_twice_throws_so_a_replayed_webhook_cannot_pay_it_again()
    {
        var order = PaidOrder();

        var ex = Assert.Throws<DomainException>(() => order.MarkPaid(order.Total, SettledAt));

        Assert.Contains("Paid -> Paid", ex.Message, StringComparison.Ordinal);
        Assert.Equal(new Money(2_680), order.Captured);
    }

    [Fact]
    public void An_illegal_transition_throws_naming_both_states_so_the_log_explains_itself()
    {
        var order = PendingOrder();

        var ex = Assert.Throws<DomainException>(order.MarkPacked);

        Assert.Contains("Illegal order transition", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Pending -> Packed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void An_unpaid_order_cannot_be_packed_or_shipped()
    {
        var order = PendingOrder();

        Assert.Throws<DomainException>(order.MarkPacked);
        Assert.Throws<DomainException>(order.MarkShipped);
    }

    [Fact]
    public void A_paid_order_cannot_skip_packing_and_ship_straight_away()
    {
        var order = PaidOrder();

        Assert.Throws<DomainException>(order.MarkShipped);
        Assert.Equal(OrderStatus.Paid, order.Status);
    }

    [Fact]
    public void The_fulfilment_path_runs_pending_to_paid_to_packed_to_shipped()
    {
        var order = PendingOrder();

        order.MarkPaid(order.Total, SettledAt);
        order.MarkPacked();
        order.MarkShipped();

        Assert.Equal(OrderStatus.Shipped, order.Status);
    }

    [Fact]
    public void A_pending_order_can_be_cancelled()
    {
        var order = PendingOrder();

        order.Cancel();

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void A_paid_order_can_still_be_cancelled_before_it_is_packed()
    {
        var order = PaidOrder();

        order.Cancel();

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void A_shipped_order_cannot_be_cancelled_because_the_goods_have_already_gone()
    {
        var order = PaidOrder();
        order.MarkPacked();
        order.MarkShipped();

        Assert.Throws<DomainException>(order.Cancel);
        Assert.Equal(OrderStatus.Shipped, order.Status);
    }

    [Fact]
    public void A_cancelled_order_is_terminal_and_refuses_every_further_move()
    {
        var order = PendingOrder();
        order.Cancel();

        Assert.Throws<DomainException>(() => order.MarkPaid(order.Total, SettledAt));
        Assert.Throws<DomainException>(order.MarkPacked);
        Assert.Throws<DomainException>(order.Cancel);
    }

    [Fact]
    public void Cancelling_a_paid_order_currently_strands_the_money_and_that_is_deliberate()
    {
        // Documents a real gap rather than hiding it: money is captured, the order is
        // cancelled, and the domain offers no refund path. Cancel-after-payment should
        // go through a refund flow, which arrives with the refunds work in a later phase.
        // If that changes, this test should fail and be rewritten, not deleted quietly.
        var order = PaidOrder();
        order.Cancel();

        var ex = Assert.Throws<DomainException>(() => order.Refund(new Money(100)));

        Assert.Contains("Cancelled", ex.Message, StringComparison.Ordinal);
        Assert.Equal(order.Captured, order.RefundableRemaining);
    }

    [Fact]
    public void Refunding_an_order_that_was_never_paid_throws()
    {
        var order = PendingOrder();

        var ex = Assert.Throws<DomainException>(() => order.Refund(new Money(100)));

        Assert.Contains("Pending", ex.Message, StringComparison.Ordinal);
        Assert.True(order.Refunded.IsZero);
    }

    [Fact]
    public void A_refund_cannot_exceed_the_amount_that_was_captured()
    {
        var order = PaidOrder();

        Assert.Throws<DomainException>(() => order.Refund(new Money(2_681)));
        Assert.True(order.Refunded.IsZero);
    }

    [Fact]
    public void Partial_refunds_accumulate_until_nothing_refundable_is_left()
    {
        var order = PaidOrder();

        order.Refund(new Money(1_000));
        order.Refund(new Money(1_680));

        Assert.Equal(new Money(2_680), order.Refunded);
        Assert.True(order.RefundableRemaining.IsZero);
        Assert.Throws<DomainException>(() => order.Refund(new Money(1)));
    }

    [Fact]
    public void RefundableRemaining_shrinks_by_each_refund()
    {
        var order = PaidOrder();

        order.Refund(new Money(1_000));

        Assert.Equal(new Money(1_000), order.Refunded);
        Assert.Equal(new Money(1_680), order.RefundableRemaining);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    public void A_refund_must_be_a_positive_amount(long amountCents)
    {
        var order = PaidOrder();

        Assert.Throws<DomainException>(() => order.Refund(new Money(amountCents)));
    }

    [Fact]
    public void A_packed_order_can_be_refunded()
    {
        var order = PaidOrder();
        order.MarkPacked();

        order.Refund(new Money(500));

        Assert.Equal(new Money(500), order.Refunded);
    }

    [Fact]
    public void A_shipped_order_can_still_be_refunded_which_is_how_returns_are_handled()
    {
        var order = PaidOrder();
        order.MarkPacked();
        order.MarkShipped();

        order.Refund(order.Captured);

        Assert.Equal(new Money(2_680), order.Refunded);
        Assert.True(order.RefundableRemaining.IsZero);
    }
}
