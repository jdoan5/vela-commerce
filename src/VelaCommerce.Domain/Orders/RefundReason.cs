namespace VelaCommerce.Domain.Orders;

/// <summary>
/// Why money went back.
/// <para>
/// Two values, because two are reachable: a shopper asked, or an order was cancelled after it had
/// already been paid for. Nothing in this system can produce a third, and a reason nobody can
/// select is a reporting category that will be wrong the first time somebody does select it.
/// </para>
/// <para>
/// Values are explicit for the same reason <see cref="OrderStatus"/>'s are: these integers are
/// persisted, so reordering the enum must not silently relabel history.
/// </para>
/// </summary>
public enum RefundReason
{
    /// <summary>The shopper asked for their money back. The goods may or may not be coming back.</summary>
    CustomerRequest = 0,

    /// <summary>
    /// The order was cancelled after payment, so the whole outstanding amount was returned as part
    /// of the cancellation. Never a partial amount: a cancellation that refunded some of the money
    /// would leave an order that is both terminated and owed.
    /// </summary>
    Cancellation = 1
}
