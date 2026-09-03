namespace VelaCommerce.Domain.Messaging;

/// <summary>
/// Where one outbox message is in its life.
/// <para>
/// The numbers are fixed rather than left to declaration order, because they are persisted as
/// integers and the claim query in the dispatcher compares against
/// <see cref="Pending"/> by value. Reordering this enum without a migration would silently
/// re-label every row in the table.
/// </para>
/// <para>
/// There is deliberately no <c>Delivering</c> state. A message that is being delivered right now
/// is one whose row is locked by <c>SELECT … FOR UPDATE SKIP LOCKED</c> inside the dispatcher's
/// transaction, and the lock is what a second replica sees — a status column could not say the
/// same thing truthfully, because a dispatcher that died mid-delivery would leave the flag set
/// forever with nothing to clear it. Letting PostgreSQL own "in flight" means a crash rolls the
/// claim back and the message becomes due again with no reaper of its own.
/// </para>
/// </summary>
public enum OutboxMessageStatus
{
    /// <summary>Not delivered yet. Due once <c>DeliverAfter</c> has passed.</summary>
    Pending = 0,

    /// <summary>The receiver answered 2xx. Terminal.</summary>
    Delivered = 1,

    /// <summary>
    /// Delivery failed often enough to stop trying. Terminal, and kept rather than deleted: the
    /// row plus its last error is the only evidence that a side effect was promised and never
    /// happened.
    /// </summary>
    Abandoned = 2,
}
