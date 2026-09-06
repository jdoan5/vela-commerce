namespace VelaCommerce.Domain.Orders;

/// <summary>
/// The single source of truth for which order transitions are legal.
/// <para>
/// Written as an explicit edge table rather than a switch, so the legal set can be
/// asserted directly in a test and rendered in the docs. Every transition the table
/// omits is illegal, including same-state self-transitions: replaying a duplicated
/// payment webhook must not move Paid to Paid, it must be recognised as a duplicate.
/// </para>
/// </summary>
public static class OrderStateMachine
{
    private static readonly HashSet<(OrderStatus From, OrderStatus To)> LegalEdges =
    [
        (OrderStatus.Pending, OrderStatus.Paid),
        (OrderStatus.Pending, OrderStatus.Cancelled),
        (OrderStatus.Paid,    OrderStatus.Packed),
        (OrderStatus.Paid,    OrderStatus.Cancelled),
        (OrderStatus.Packed,  OrderStatus.Shipped)
    ];

    /// <summary>All legal edges, for tests and documentation.</summary>
    public static IReadOnlyCollection<(OrderStatus From, OrderStatus To)> Edges => LegalEdges;

    /// <summary>
    /// The statuses in which the stock ledger is still holding this order's units, so anything that
    /// removes the order has to hand them back first.
    /// <para>
    /// The two omissions are the whole subtlety, and they are omitted for opposite reasons.
    /// <see cref="OrderStatus.Cancelled"/> released its reservations on the way in, so there is
    /// nothing left to give back. <see cref="OrderStatus.Shipped"/> is the dangerous one:
    /// <c>OrderTimelineWorker</c> ships by decrementing <c>reserved</c> and <c>on_hand</c> together
    /// and leaves the reservation row <em>Confirmed</em> rather than Released — so a caller that
    /// released "every reservation that is not Released" would decrement <c>reserved</c> a second
    /// time for units that already left the building, quietly stealing them from whoever holds the
    /// next reservation on that variant. The ledger would not go negative — the guarded UPDATE and
    /// <c>ck_stock_items_reserved_non_negative</c> both stand in the way — which is exactly what
    /// would make it hard to notice.
    /// </para>
    /// <para>
    /// This is a fact about orders, not about either caller, and it now HAS two callers: the
    /// visitor's own reset in <c>DemoEndpoints</c> and the stale-data purge in
    /// <c>DemoSessionPurge</c>. It lived as a private array beside the first of them until the
    /// second one needed it. A rule this easy to get wrong must not be stated twice, because the
    /// copy that drifts is the one nobody is reading.
    /// </para>
    /// </summary>
    public static IReadOnlyList<OrderStatus> HoldingStock => StatesHoldingStock;

    private static readonly OrderStatus[] StatesHoldingStock =
    [
        OrderStatus.Pending,
        OrderStatus.Paid,
        OrderStatus.Packed,
    ];

    public static bool IsLegal(OrderStatus from, OrderStatus to) => LegalEdges.Contains((from, to));

    /// <summary>Statuses from which no transition is possible.</summary>
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Shipped or OrderStatus.Cancelled;

    public static IEnumerable<OrderStatus> NextFrom(OrderStatus from) =>
        LegalEdges.Where(e => e.From == from).Select(e => e.To);
}
