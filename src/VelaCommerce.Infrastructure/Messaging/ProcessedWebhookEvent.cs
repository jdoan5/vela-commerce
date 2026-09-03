namespace VelaCommerce.Infrastructure.Messaging;

/// <summary>
/// Proof that one webhook event has already been applied. The receiver's dedupe key.
/// <para>
/// At-least-once delivery is not a defect to be engineered away — it is what every real gateway
/// promises, and what this repository's own dispatcher promises too, because a deliverer that
/// crashes between "the receiver said 200" and "the row says Delivered" has no honest option but
/// to send again. Exactly-once <em>effect</em> is therefore built at the receiving end, out of
/// two at-least-once halves: insert this row and apply the order transition in the SAME
/// transaction. The second delivery loses on the primary key, its transaction rolls back with the
/// transition inside it, and the receiver answers 200 so the sender stops retrying. No lock, no
/// "have I seen this?" query, no window between checking and acting.
/// </para>
/// <para>
/// <b>Why this type lives in Infrastructure and not in the domain.</b> A processed-event ledger is
/// a fact about a transport, not about commerce. The domain's defence against a replayed
/// settlement is a different and stronger one — <c>OrderStateMachine</c> has no <c>Paid -&gt;
/// Paid</c> edge, so a duplicate that somehow got past this table would still be refused by the
/// aggregate. Two independent mechanisms, deliberately: this one makes a duplicate cheap and
/// silent, the state machine makes it impossible.
/// </para>
/// <para>
/// <b>Owned here, written there.</b> The webhook receiver inserts these rows; this table and its
/// mapping ship with the outbox migration so that one migration covers the phase and two agents
/// cannot both add one and collide on the model snapshot.
/// </para>
/// </summary>
public sealed class ProcessedWebhookEvent
{
    /// <summary>Matches the <c>event_id</c> column width. Gateway event ids are opaque and short.</summary>
    public const int MaxEventIdLength = 128;

    private ProcessedWebhookEvent() { } // EF

    /// <summary>
    /// Records that <paramref name="eventId"/> has been handled.
    /// </summary>
    /// <param name="eventId">
    /// The <em>gateway's</em> id for the event, not one we mint. That is the whole mechanism: two
    /// deliveries of one event carry one id, so the second insert collides. An id derived from the
    /// body's content would dedupe two genuinely different events that happened to say the same
    /// thing, and an id minted on arrival would dedupe nothing at all.
    /// </param>
    /// <param name="receivedAt">When this delivery arrived. Not when the gateway says it happened.</param>
    /// <param name="eventType">Optional, for reading the table by eye.</param>
    /// <param name="orderReference">Optional; the order this event settled, for the same reason.</param>
    public ProcessedWebhookEvent(
        string eventId,
        DateTimeOffset receivedAt,
        string? eventType = null,
        string? orderReference = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        EventId = eventId;
        ReceivedAt = receivedAt;
        EventType = eventType;
        OrderReference = orderReference;
    }

    /// <summary>The primary key. Uniqueness here is the dedupe.</summary>
    public string EventId { get; private set; } = null!;

    /// <summary>When the delivery that won arrived.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Nullable so a receiver that only has an id can still record it.</summary>
    public string? EventType { get; private set; }

    /// <summary>Nullable for the same reason as <see cref="EventType"/>.</summary>
    public string? OrderReference { get; private set; }
}
