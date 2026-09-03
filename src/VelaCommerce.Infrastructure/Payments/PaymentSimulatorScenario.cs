namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// The paths a reviewer can drive the simulator down.
/// <para>
/// This vocabulary is deliberately confined to Infrastructure. The domain port carries a free-text
/// <c>ScenarioHint</c> it never interprets, so a real gateway adapter can ignore the hint entirely
/// and nothing in the domain has to know that a simulator exists.
/// </para>
/// <para>
/// Values are explicit because they are surfaced in the Demo Lab's permalinks and may end up in a
/// stored request log; reordering the enum must not repoint a saved link.
/// </para>
/// </summary>
public enum PaymentSimulatorScenario
{
    /// <summary>Synchronous capture. The happy path, and the default when nothing else matches.</summary>
    Succeed = 0,

    /// <summary>Synchronous refusal. No webhook follows, because nothing is going to settle.</summary>
    Decline = 1,

    /// <summary>The shopper walks away. No capture, no webhook, and the reservation is left to lapse.</summary>
    Abandon = 2,

    /// <summary>
    /// One settlement, delivered twice with the same event id and the same signature. Proves the
    /// receiver dedupes on the provider's event id rather than on arrival count.
    /// </summary>
    Duplicate = 3,

    /// <summary>
    /// One settlement, delivered after a pause. The ordinary asynchronous happy path, and the one
    /// that exercises "order stays Pending, UI says confirming payment" honestly.
    /// </summary>
    Delay = 4,

    /// <summary>
    /// Two settlement events delivered in the opposite order to the one they were raised in.
    /// Proves correctness comes from the order state machine refusing backwards edges, not from
    /// trusting the network to preserve sequence.
    /// </summary>
    Reorder = 5
}
