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
    private static readonly DateTimeOffset PlacedAt = new(2026, 3, 14, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SettledAt = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private const long ShippingCents = 500;
    private const long TaxCents = 180;

    /// <summary>
    /// Stands in for the gateway's identifier for the capture. Required by MarkPaid because an
    /// order that took money it cannot name is an order that can never be refunded.
    /// </summary>
    private const string PaymentRef = "pay_test_reference";

    /// <summary>
    /// Records a refund with the bookkeeping every real caller supplies. The aggregate does not
    /// police key uniqueness — a unique index does — so these keys only have to be plausible.
    /// </summary>
    private static Refund Refund(Order order, long amountCents, string key = "refund-key-1") =>
        order.IssueRefund(
            new Money(amountCents),
            RefundReason.CustomerRequest,
            key,
            "rfnd_test_reference",
            restockedUnits: 0,
            SettledAt);

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
        new Money(TaxCents),
        PlacedAt);

    private static Order PaidOrder()
    {
        var order = PendingOrder();
        order.MarkPaid(order.Total, PaymentRef, SettledAt);
        return order;
    }

    [Fact]
    public void An_order_copies_every_cart_line_including_the_name_and_sku_it_was_sold_under()
    {
        var cart = new Cart(Guid.CreateVersion7());
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);

        var order = Order.FromCart(cart, "VC-1001", "idempotency-1001", ValidAddress(), new Money(500), new Money(360), PlacedAt);

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

        var order = Order.FromCart(cart, "VC-1002", "idempotency-1002", ValidAddress(), Money.Zero(), Money.Zero(), PlacedAt);

        Assert.Equal(cart.DemoSessionId, order.DemoSessionId);
        Assert.Equal(cart.Currency, order.Currency);
    }

    [Fact]
    public void An_empty_cart_cannot_become_an_order()
    {
        var cart = new Cart(Guid.CreateVersion7());

        Assert.Throws<DomainException>(() =>
        {
            _ = Order.FromCart(cart, "VC-1003", "idempotency-1003", ValidAddress(), Money.Zero(), Money.Zero(), PlacedAt);
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
            _ = Order.FromCart(cart, "VC-1004", key!, ValidAddress(), Money.Zero(), Money.Zero(), PlacedAt);
        });
    }

    [Fact]
    public void An_order_cannot_be_placed_against_an_incomplete_shipping_address()
    {
        var cart = CartWith((1_000L, 1));
        var addressWithNoRecipient = ValidAddress() with { Recipient = "  " };

        Assert.Throws<DomainException>(() =>
        {
            _ = Order.FromCart(cart, "VC-1005", "idempotency-1005", addressWithNoRecipient, Money.Zero(), Money.Zero(), PlacedAt);
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
            new Money(TaxCents),
            PlacedAt);

        Assert.Equal(new Money(4_500), order.Subtotal);
        Assert.Equal(new Money(4_500 + ShippingCents + TaxCents), order.Total);
    }

    [Fact]
    public void Settling_a_pending_order_records_the_capture_and_the_moment_it_happened()
    {
        var order = PendingOrder();

        order.MarkPaid(order.Total, PaymentRef, SettledAt);

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

        Assert.Throws<DomainException>(() => order.MarkPaid(new Money(capturedCents), PaymentRef, SettledAt));
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void A_negative_capture_is_refused()
    {
        var order = PendingOrder();

        Assert.Throws<DomainException>(() => order.MarkPaid(new Money(-2_680), PaymentRef, SettledAt));
    }

    [Fact]
    public void Settling_the_same_order_twice_throws_so_a_replayed_webhook_cannot_pay_it_again()
    {
        var order = PaidOrder();

        var ex = Assert.Throws<DomainException>(() => order.MarkPaid(order.Total, PaymentRef, SettledAt));

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

        order.MarkPaid(order.Total, PaymentRef, SettledAt);
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
    public void A_paid_order_that_has_already_been_refunded_can_be_cancelled_outright()
    {
        // Cancel() guards on money outstanding rather than on status, so an order whose funds are
        // already back is cancellable by the plain path. The Paid -> Cancelled edge is intact; what
        // changed is that taking it now requires owing nothing.
        var order = PaidOrder();
        Refund(order, order.Captured.Amount);

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

        Assert.Throws<DomainException>(() => order.MarkPaid(order.Total, PaymentRef, SettledAt));
        Assert.Throws<DomainException>(order.MarkPacked);
        Assert.Throws<DomainException>(order.Cancel);
    }

    [Fact]
    public void The_placed_at_timestamp_comes_from_the_caller_not_the_ambient_clock()
    {
        // Regression: Order's constructor originally read DateTimeOffset.UtcNow, which an
        // architecture test caught. An aggregate that reads the clock cannot be driven by
        // the demo's accelerated timeline, and cannot be asserted on here.
        var order = PendingOrder();

        Assert.Equal(PlacedAt, order.PlacedAt);
        Assert.True(order.PlacedAt < SettledAt);
    }

    [Fact]
    public void Cancelling_a_paid_order_is_refused_because_it_would_strand_the_money()
    {
        // The rewrite of Cancelling_a_paid_order_currently_strands_the_money_and_that_is_deliberate,
        // which documented this as an open gap and asked to be rewritten rather than deleted when it
        // closed. What it described really happened: Cancel() took the Paid -> Cancelled edge, and
        // the money became unreturnable, because a cancelled order refuses refunds. Cancel() now
        // refuses instead, and names the method that does both.
        var order = PaidOrder();

        var ex = Assert.Throws<DomainException>(order.Cancel);

        Assert.Contains("CancelAndRefund", ex.Message, StringComparison.Ordinal);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(order.Captured, order.RefundableRemaining);
    }

    [Fact]
    public void Cancelling_a_paid_order_through_the_refund_path_returns_everything_and_terminates_it()
    {
        var order = PaidOrder();

        var refund = order.CancelAndRefund("cancel-key-1", "rfnd_test_reference", restockedUnits: 3, SettledAt);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(order.Captured, order.Refunded);
        Assert.True(order.RefundableRemaining.IsZero);
        Assert.Equal(RefundReason.Cancellation, refund.Reason);
        Assert.Equal(3, refund.RestockedUnits);
        Assert.Single(order.Refunds);
    }

    [Fact]
    public void A_cancellation_that_the_state_machine_would_refuse_returns_no_money()
    {
        // Order of operations inside CancelAndRefund: an illegal transition must be caught before
        // anything moves, or a shipped order would be refunded and then left Shipped anyway - money
        // gone, with nothing recording why.
        var order = PaidOrder();
        order.MarkPacked();
        order.MarkShipped();

        Assert.Throws<DomainException>(
            () => order.CancelAndRefund("cancel-key-1", "rfnd_test_reference", restockedUnits: 0, SettledAt));

        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.True(order.Refunded.IsZero);
        Assert.Empty(order.Refunds);
    }

    [Fact]
    public void A_refund_is_recorded_on_the_ledger_and_not_only_in_the_running_total()
    {
        var order = PaidOrder();

        var refund = Refund(order, 1_000, "refund-key-1");

        var only = Assert.Single(order.Refunds);
        Assert.Same(refund, only);
        Assert.Equal(new Money(1_000), only.Amount);
        Assert.Equal(RefundReason.CustomerRequest, only.Reason);
        Assert.Equal(order.Id, only.OrderId);
        Assert.Equal(SettledAt, only.RefundedAt);
    }

    [Fact]
    public void The_running_total_is_always_the_sum_of_the_ledger()
    {
        // The column exists so a CHECK constraint has something to compare against without summing
        // a child table. That is only safe while the two agree.
        var order = PaidOrder();

        Refund(order, 1_000, "refund-key-1");
        Refund(order, 500, "refund-key-2");

        var summed = order.Refunds.Select(entry => entry.Amount).Aggregate(static (a, b) => a + b);

        Assert.Equal(summed, order.Refunded);
    }

    [Fact]
    public void Settling_an_order_records_the_payment_it_can_later_be_refunded_against()
    {
        var order = PendingOrder();

        order.MarkPaid(order.Total, PaymentRef, SettledAt);

        Assert.Equal(PaymentRef, order.PaymentReference);
    }

    [Fact]
    public void An_order_cannot_be_settled_without_naming_the_payment_that_settled_it()
    {
        var order = PendingOrder();

        Assert.Throws<DomainException>(() => order.MarkPaid(order.Total, "  ", SettledAt));
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Null(order.PaymentReference);
    }

    [Fact]
    public void A_fully_refunded_order_keeps_the_status_its_fulfilment_actually_reached()
    {
        // A parcel that shipped did ship, whoever ended up paying for it. Collapsing a full refund
        // into Cancelled would lose the fact that goods are in the world.
        var order = PaidOrder();
        order.MarkPacked();
        order.MarkShipped();

        Refund(order, order.Captured.Amount);

        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.True(order.IsFullyRefunded);
    }

    [Fact]
    public void Refunding_an_order_that_was_never_paid_throws()
    {
        var order = PendingOrder();

        var ex = Assert.Throws<DomainException>(() => Refund(order, 100));

        Assert.Contains("Pending", ex.Message, StringComparison.Ordinal);
        Assert.True(order.Refunded.IsZero);
    }

    [Fact]
    public void A_refund_cannot_exceed_the_amount_that_was_captured()
    {
        var order = PaidOrder();

        Assert.Throws<DomainException>(() => Refund(order, 2_681));
        Assert.True(order.Refunded.IsZero);
    }

    [Fact]
    public void Partial_refunds_accumulate_until_nothing_refundable_is_left()
    {
        var order = PaidOrder();

        Refund(order, 1_000, "refund-key-1");
        Refund(order, 1_680, "refund-key-2");

        Assert.Equal(new Money(2_680), order.Refunded);
        Assert.True(order.RefundableRemaining.IsZero);
        Assert.Throws<DomainException>(() => Refund(order, 1));
    }

    [Fact]
    public void RefundableRemaining_shrinks_by_each_refund()
    {
        var order = PaidOrder();

        Refund(order, 1_000);

        Assert.Equal(new Money(1_000), order.Refunded);
        Assert.Equal(new Money(1_680), order.RefundableRemaining);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    public void A_refund_must_be_a_positive_amount(long amountCents)
    {
        var order = PaidOrder();

        Assert.Throws<DomainException>(() => Refund(order, amountCents));
    }

    [Fact]
    public void A_packed_order_can_be_refunded()
    {
        var order = PaidOrder();
        order.MarkPacked();

        Refund(order, 500);

        Assert.Equal(new Money(500), order.Refunded);
    }

    [Fact]
    public void A_shipped_order_can_still_be_refunded_which_is_how_returns_are_handled()
    {
        var order = PaidOrder();
        order.MarkPacked();
        order.MarkShipped();

        Refund(order, order.Captured.Amount);

        Assert.Equal(new Money(2_680), order.Refunded);
        Assert.True(order.RefundableRemaining.IsZero);
    }
}
