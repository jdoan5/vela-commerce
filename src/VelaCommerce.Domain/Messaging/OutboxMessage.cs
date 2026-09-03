using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Messaging;

/// <summary>
/// A side effect that has been promised and not yet performed, recorded in the same transaction as
/// the state change that promised it.
/// <para>
/// This is the whole point of the transactional outbox, and it is worth stating as a claim about
/// facts rather than as a pattern name. "The payment was authorized" and "a settlement
/// notification will arrive" are two halves of one fact. Write the first to the database and
/// perform the second over HTTP and they can disagree in both directions: commit then crash, and
/// the notification never happens; send then fail to commit, and a notification arrives for an
/// order that does not exist. Writing a row instead makes the promise part of the same commit, so
/// the two can only be true together or false together. Delivery then becomes a separate,
/// retryable problem — which is a much easier problem, because it no longer has to be atomic with
/// anything.
/// </para>
/// <para>
/// <b>The payload is a string, not an object, and that is load-bearing.</b> What is stored are the
/// exact bytes somebody already signed. Anything that reconstructs the body from a deserialized
/// object — a different property order, a different escape, one extra space — produces a different
/// message under the same signature, and the receiver reports a security failure for what is
/// really a serialization difference. So the payload arrives here as text and leaves as text, and
/// nothing in this type knows what it means.
/// </para>
/// <para>
/// <b>Free of infrastructure by construction.</b> No HTTP, no JSON, no EF. The retry schedule and
/// the attempt cap are passed in as plain values, the same way every other aggregate here takes
/// <c>now</c> as a parameter instead of reading a clock: what abandoning <em>means</em> is a rule
/// worth keeping in one place, but how long to wait and how many times to try are deployment
/// decisions and belong in configuration.
/// </para>
/// <para>
/// <b>Not derived from <see cref="Entity"/>, deliberately.</b> That base carries soft delete, and
/// an outbox row has no such concept: its life ends in a terminal <see cref="OutboxMessageStatus"/>
/// that the dispatcher's claim query already filters on. A <c>deleted_at</c> column here would be
/// a column nothing writes, and the query filter that comes with it would wrap the dispatcher's
/// <c>FOR UPDATE SKIP LOCKED</c> statement in a subquery — turning the one piece of SQL that has
/// to reach PostgreSQL verbatim into something EF composes. The identifier is still a UUIDv7 for
/// the same reason every other key here is: it sorts by creation time, so the index the dispatcher
/// scans stays dense.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// How much of a failure is kept. Long enough for a stack-trace-free exception message and an
    /// HTTP status line; short enough that a poisoned message cannot fill the table with one
    /// enormous string per attempt.
    /// </summary>
    public const int MaxErrorLength = 1024;

    private OutboxMessage() { } // EF

    /// <summary>
    /// Records a message that must be delivered, at or after <paramref name="deliverAfter"/>.
    /// </summary>
    /// <param name="messageType">
    /// What this is, for logging and for a future dispatcher that routes by type. Opaque to this
    /// class on purpose — the outbox must not need a new case statement to carry a new event.
    /// </param>
    /// <param name="payload">The exact body to transmit, byte for byte. Never re-serialized.</param>
    /// <param name="signatureHeader">
    /// The complete header value that authenticates <paramref name="payload"/>. Stored alongside
    /// it rather than recomputed at send time, because recomputing needs the signing secret in the
    /// dispatcher and would re-sign whatever bytes it was handed — including corrupted ones.
    /// </param>
    /// <param name="deliverAfter">
    /// The earliest instant this may be sent. An absolute instant, not a delay: a delay would be
    /// relative to a clock the dispatcher never saw.
    /// </param>
    /// <param name="now">When this message was created.</param>
    public OutboxMessage(
        string messageType,
        string payload,
        string signatureHeader,
        DateTimeOffset deliverAfter,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(messageType))
            throw new DomainException("An outbox message must say what type it is.");

        if (string.IsNullOrEmpty(payload))
            throw new DomainException("An outbox message must carry a payload; there is nothing to deliver otherwise.");

        if (string.IsNullOrWhiteSpace(signatureHeader))
            throw new DomainException(
                "An outbox message must carry the signature header for its payload. A receiver that "
                + "verifies signatures rejects an unsigned body, so enqueuing one would guarantee a "
                + "delivery that can never succeed.");

        MessageType = messageType;
        Payload = payload;
        SignatureHeader = signatureHeader;
        DeliverAfter = deliverAfter;
        Status = OutboxMessageStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>UUIDv7: unique, and time-ordered so the dispatcher's index does not fragment.</summary>
    public Guid Id { get; private set; } = Guid.CreateVersion7();

    /// <summary>What kind of message this is. Carried through to logs, never interpreted here.</summary>
    public string MessageType { get; private set; } = null!;

    /// <summary>The exact body to transmit. See the type's remarks for why this is text.</summary>
    public string Payload { get; private set; } = null!;

    /// <summary>The signature header that authenticates <see cref="Payload"/>.</summary>
    public string SignatureHeader { get; private set; } = null!;

    /// <summary>
    /// The earliest instant this may be delivered. Also the retry schedule: a failed attempt
    /// pushes it into the future, which is what takes a poisoned message out of the way of
    /// everything queued behind it.
    /// </summary>
    public DateTimeOffset DeliverAfter { get; private set; }

    /// <summary>Delivery attempts made, successful or not. The cap is counted against this.</summary>
    public int Attempts { get; private set; }

    public OutboxMessageStatus Status { get; private set; }

    /// <summary>
    /// Why the last attempt failed, truncated to <see cref="MaxErrorLength"/>. Kept after
    /// abandonment — an abandoned message with no reason is a mystery rather than a record.
    /// </summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When this row last changed. Distinct from <see cref="DeliveredAt"/>, which only a success sets.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>Whether this message is waiting and its time has come.</summary>
    public bool IsDue(DateTimeOffset now) =>
        Status == OutboxMessageStatus.Pending && DeliverAfter <= now;

    /// <summary>
    /// The receiver accepted it. Terminal.
    /// <para>
    /// Refuses to re-mark a message that has already finished. That is not defensiveness for its
    /// own sake: two dispatchers delivering one message twice is the failure this table's claim
    /// mechanism exists to prevent, so if it ever happens the second one must fail loudly here
    /// rather than quietly overwrite the first delivery's timestamp and hide the evidence.
    /// </para>
    /// </summary>
    public void MarkDelivered(DateTimeOffset now)
    {
        RefuseIfSettled(nameof(MarkDelivered));

        Attempts++;
        Status = OutboxMessageStatus.Delivered;
        DeliveredAt = now;
        LastError = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// An attempt failed. Schedules the next one, or gives up if this was the last allowed.
    /// </summary>
    /// <param name="error">What went wrong. Truncated, never discarded.</param>
    /// <param name="retryAt">
    /// When to try again. Ignored once the cap is reached — computed by the caller, because the
    /// backoff curve is a deployment decision and this type is not the place to encode one.
    /// </param>
    /// <param name="maxAttempts">How many attempts in total are allowed before abandoning.</param>
    /// <param name="now">The instant of this attempt.</param>
    public void RecordFailure(string error, DateTimeOffset retryAt, int maxAttempts, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        RefuseIfSettled(nameof(RecordFailure));

        Attempts++;
        LastError = Truncate(error);
        UpdatedAt = now;

        if (Attempts >= maxAttempts)
        {
            // Abandoned rather than retried forever. A message the receiver will never accept —
            // a payload it cannot parse, a signature it will not verify — is not made more
            // deliverable by trying again, and a queue that keeps retrying one of those is a
            // queue that eventually delivers nothing else. The row and its last error stay.
            Status = OutboxMessageStatus.Abandoned;
            return;
        }

        DeliverAfter = retryAt;
    }

    /// <summary>
    /// Pushes the message into the future without spending an attempt.
    /// <para>
    /// For answers that describe the receiver rather than the message — 429 because it is rate
    /// limited, 5xx because it is unwell. Neither says the payload is wrong, so neither should
    /// count toward abandonment. Letting them would mean anyone able to reach the public
    /// webhook route could permanently abandon real captured settlements by flooding it, which
    /// is exactly what this queue exists to make impossible.
    /// </para>
    /// </summary>
    public void Defer(string error, DateTimeOffset retryAt, DateTimeOffset now)
    {
        RefuseIfSettled(nameof(Defer));

        LastError = Truncate(error);
        UpdatedAt = now;
        DeliverAfter = retryAt;
    }

    /// <summary>
    /// Only a <see cref="OutboxMessageStatus.Pending"/> message can be delivered or fail. Anything
    /// else means two deliverers agreed on the same row, which is exactly what must not happen
    /// silently.
    /// </summary>
    private void RefuseIfSettled(string operation)
    {
        if (Status is not OutboxMessageStatus.Pending)
            throw new DomainException(
                $"{operation} was called on outbox message {Id}, which is already {Status}. A "
                + "message leaves Pending exactly once; reaching this means two deliveries claimed "
                + "the same row.");
    }

    /// <summary>
    /// Keeps the head of the message, which is where the cause is. Null and blank are stored as
    /// null so that "failed for no stated reason" reads the same way in every row.
    /// </summary>
    private static string? Truncate(string? error) =>
        string.IsNullOrWhiteSpace(error)
            ? null
            : error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
