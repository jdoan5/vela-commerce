namespace VelaCommerce.Infrastructure.Fulfilment;

/// <summary>
/// The two moves the fulfilment worker is allowed to make.
/// <para>
/// Deliberately not a mirror of <c>OrderStatus</c>. This names the <em>edges</em> the worker
/// drives — <c>Paid -&gt; Packed</c> and <c>Packed -&gt; Shipped</c> — and the set is closed at
/// two because those are the only edges in <c>OrderStateMachine</c> that nothing else in the
/// system triggers. Payment settles <c>Pending -&gt; Paid</c>; a shopper or the reaper drives
/// <c>-&gt; Cancelled</c>. A worker that could express a third move would be a worker that could
/// invent one.
/// </para>
/// </summary>
public enum OrderTimelineStep
{
    /// <summary>Paid to Packed. Picked and boxed; nothing leaves the warehouse yet.</summary>
    Pack = 0,

    /// <summary>Packed to Shipped. This is the step that actually moves stock.</summary>
    Ship = 1
}
