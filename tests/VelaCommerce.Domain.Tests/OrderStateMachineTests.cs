using VelaCommerce.Domain.Orders;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// The order lifecycle is asserted as a table rather than as a handful of happy paths, so
/// that adding an edge to the production set breaks a test instead of quietly widening what
/// an order is allowed to do. The expected edges are restated here on purpose: a test that
/// read them back out of <see cref="OrderStateMachine"/> would agree with any mistake.
/// </summary>
public sealed class OrderStateMachineTests
{
    private static readonly HashSet<(OrderStatus From, OrderStatus To)> DocumentedEdges =
    [
        (OrderStatus.Pending, OrderStatus.Paid),
        (OrderStatus.Pending, OrderStatus.Cancelled),
        (OrderStatus.Paid, OrderStatus.Packed),
        (OrderStatus.Paid, OrderStatus.Cancelled),
        (OrderStatus.Packed, OrderStatus.Shipped)
    ];

    public static TheoryData<OrderStatus, OrderStatus, bool> EveryTransitionPair()
    {
        var data = new TheoryData<OrderStatus, OrderStatus, bool>();
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
                data.Add(from, to, DocumentedEdges.Contains((from, to)));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTransitionPair))]
    public void Every_transition_the_table_omits_is_illegal(OrderStatus from, OrderStatus to, bool expected)
    {
        Assert.Equal(expected, OrderStateMachine.IsLegal(from, to));
    }

    [Fact]
    public void The_published_edge_set_is_exactly_the_five_documented_edges()
    {
        var published = OrderStateMachine.Edges.ToHashSet();

        Assert.Equal(DocumentedEdges.Count, published.Count);
        Assert.All(DocumentedEdges, edge => Assert.Contains(edge, published));
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    public void A_status_can_never_transition_to_itself(OrderStatus status)
    {
        Assert.False(OrderStateMachine.IsLegal(status, status));
    }

    [Fact]
    public void Paid_to_paid_is_illegal_which_is_what_makes_a_replayed_payment_webhook_detectable()
    {
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Paid, OrderStatus.Paid));
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    public void Shipped_and_cancelled_are_terminal_and_offer_no_next_step(OrderStatus terminal)
    {
        Assert.True(OrderStateMachine.IsTerminal(terminal));
        Assert.Empty(OrderStateMachine.NextFrom(terminal));
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Packed)]
    public void A_status_with_work_remaining_is_not_terminal(OrderStatus status)
    {
        Assert.False(OrderStateMachine.IsTerminal(status));
        Assert.NotEmpty(OrderStateMachine.NextFrom(status));
    }

    [Fact]
    public void A_pending_order_may_only_be_paid_or_cancelled()
    {
        var next = OrderStateMachine.NextFrom(OrderStatus.Pending).ToArray();

        Assert.Equal(2, next.Length);
        Assert.Contains(OrderStatus.Paid, next);
        Assert.Contains(OrderStatus.Cancelled, next);
    }

    [Fact]
    public void A_paid_order_may_only_be_packed_or_cancelled()
    {
        var next = OrderStateMachine.NextFrom(OrderStatus.Paid).ToArray();

        Assert.Equal(2, next.Length);
        Assert.Contains(OrderStatus.Packed, next);
        Assert.Contains(OrderStatus.Cancelled, next);
    }

    [Fact]
    public void A_packed_order_may_only_ship_because_money_has_already_changed_hands()
    {
        var next = OrderStateMachine.NextFrom(OrderStatus.Packed).ToArray();

        Assert.Equal(OrderStatus.Shipped, Assert.Single(next));
    }

    [Fact]
    public void Payment_cannot_be_skipped_on_the_way_to_fulfilment()
    {
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Pending, OrderStatus.Packed));
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Pending, OrderStatus.Shipped));
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Paid, OrderStatus.Shipped));
    }

    [Fact]
    public void An_order_can_never_move_backwards_through_the_lifecycle()
    {
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Paid, OrderStatus.Pending));
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Packed, OrderStatus.Paid));
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Shipped, OrderStatus.Packed));
    }

    [Fact]
    public void A_shipped_order_cannot_be_cancelled_because_the_goods_have_already_gone()
    {
        Assert.False(OrderStateMachine.IsLegal(OrderStatus.Shipped, OrderStatus.Cancelled));
    }
}
