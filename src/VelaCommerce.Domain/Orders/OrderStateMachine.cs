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

    public static bool IsLegal(OrderStatus from, OrderStatus to) => LegalEdges.Contains((from, to));

    /// <summary>Statuses from which no transition is possible.</summary>
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Shipped or OrderStatus.Cancelled;

    public static IEnumerable<OrderStatus> NextFrom(OrderStatus from) =>
        LegalEdges.Where(e => e.From == from).Select(e => e.To);
}
