using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using VelaCommerce.Domain.Messaging;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Infrastructure.Messaging;

/// <summary>
/// Drains the outbox: finds messages whose time has come, posts each to the receiver, and writes
/// down what happened.
/// <para>
/// Checkout's job ended when it committed a promise. This is the half that keeps it, and the two
/// are deliberately not the same transaction, the same request or even the same failure domain —
/// which is what lets the promise survive a process that dies one line after the commit.
/// </para>
///
/// <para><b>Three things here are easy to get wrong, so each is stated as a rule.</b></para>
///
/// <para>
/// <b>1. The stored bytes are transmitted unchanged.</b> The body was signed before it was stored,
/// so the payload is read as text, encoded once with UTF-8 and handed to
/// <see cref="OutboxDeliveryClient"/> as bytes. It is never deserialized, never re-serialized and
/// never passed to anything that would serialize it on this code's behalf. Nothing in this
/// namespace so much as references a JSON serializer, and the delivery client's parameter is a
/// byte array precisely so that handing it an object is a compile error rather than a signature
/// mismatch three seconds later. There is an explicit round-trip check below as well.
/// </para>
///
/// <para>
/// <b>2. Two dispatchers never deliver the same message.</b> Each message is claimed with
/// <c>SELECT … FOR UPDATE SKIP LOCKED</c> inside its own transaction: the row is locked for the
/// length of the delivery, and a second replica scanning the same index skips it rather than
/// waiting for it. This is worth the trouble even for a demo, because the alternative — read a
/// batch, then update it — has a window between the read and the write in which both replicas
/// have the same batch, and that window is where duplicate charges come from in real systems.
/// PostgreSQL is also the only participant that can decide this correctly: a status column saying
/// "delivering" would survive the crash of the process that set it, whereas a lock does not.
/// </para>
/// <para>
/// The cost is honest: the row lock is held across an HTTP request, which is the thing this
/// codebase's checkout handler goes out of its way to avoid. The difference is what is locked. A
/// checkout holds stock rows that other shoppers are queuing for; this holds one outbox row that
/// nothing else reads except another dispatcher, which skips it. The exposure is bounded by
/// <see cref="OutboxOptions.DeliveryTimeout"/> and by delivering one message per transaction
/// rather than a batch, so a stalled receiver delays one row, not a sweep's worth.
/// </para>
///
/// <para>
/// <b>3. One bad message cannot stop the queue.</b> A failure is recorded, not thrown: the attempt
/// count rises, the last error is kept, and <c>DeliverAfter</c> moves into the future by an
/// exponential backoff — which is what takes the failing message out of the way of everything
/// behind it, because the claim query orders by exactly that column. Past
/// <see cref="OutboxOptions.MaxAttempts"/> it is abandoned with its error intact. The sweep loop
/// swallows everything else the same way <c>ReservationReaper</c> does, because a background
/// service that dies silently is worse than one that fails loudly and tries again.
/// </para>
///
/// <para>
/// <b>On tenancy.</b> <c>ReservationReaper</c> has to say
/// <c>IgnoreQueryFilters([DemoTenancyFilter])</c> because it reads orders, and a background worker
/// has no visitor for the filter to match — it fails closed, so an unfiltered read there sees
/// nothing at all. The outbox table carries no filters by design (see <see cref="OutboxMessage"/>),
/// which is why nothing here needs that call: a settlement notification belongs to a payment, not
/// to a browser session, and a filter on this table would make the dispatcher blind in exactly the
/// same silent way.
/// </para>
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxOptions options,
    OutboxDeliveryClient deliveryClient,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "The outbox dispatcher is disabled by configuration ({Key}). Messages will be enqueued and "
                + "not delivered.",
                $"{OutboxOptions.SectionName}:{nameof(OutboxOptions.Enabled)}");

            return;
        }

        if (options.ReceiverUrl is not { } receiver)
        {
            // Declining to run beats guessing a port. A guessed receiver is not an inert mistake:
            // every message would fail against it, and five failures is an abandoned message, so
            // guessing wrong would discard settlements rather than delay them. Left alone they
            // stay Pending and are delivered by the first correctly-configured process to start.
            logger.LogWarning(
                "The outbox dispatcher cannot tell where to deliver: no {Key} is configured and this host "
                + "publishes no address to derive one from. Messages will stay Pending until one of those is "
                + "true. Set {Key} to the webhook receiver's absolute URL.",
                $"{OutboxOptions.SectionName}:{nameof(OutboxOptions.ReceiverUrl)}",
                $"{OutboxOptions.SectionName}:{nameof(OutboxOptions.ReceiverUrl)}");

            return;
        }

        logger.LogInformation(
            "The outbox dispatcher is delivering to {Receiver} every {PollInterval}.",
            receiver,
            options.PollInterval);

        // A sweep on boot matters here for the same reason it does in the reaper: a container that
        // restarted between the commit and the delivery is precisely the case the outbox exists
        // for, and nothing else would notice until the next tick.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(receiver, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Never let one bad sweep kill the service. Whatever this was — a connection lost,
                // a migration mid-flight — the messages are still in the table and the next tick
                // will find them.
                logger.LogError(exception, "An outbox sweep failed. Retrying at the next interval.");
            }

            try
            {
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Delivers up to <see cref="OutboxOptions.BatchSize"/> due messages, one transaction each.
    /// Public so a test can drive a sweep without waiting for a timer.
    /// </summary>
    /// <returns>What the sweep did, for logging and for tests to assert on.</returns>
    public async Task<OutboxSweepResult> SweepAsync(Uri receiver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        // One scope per sweep, like the reaper: the context is scoped, and a background service
        // that resolved one from the root provider would keep a single change tracker (and a
        // single connection) alive for the life of the process.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();

        var result = OutboxSweepResult.Empty;

        for (var processed = 0; processed < options.BatchSize; processed++)
        {
            var step = await DeliverNextAsync(db, receiver, cancellationToken);

            if (step is OutboxSweepStep.Idle)
                break;

            result = result.With(step);
        }

        if (result.Attempted > 0)
        {
            logger.LogInformation(
                "Outbox sweep: {Delivered} delivered, {Retrying} to retry, {Abandoned} abandoned.",
                result.Delivered,
                result.Retrying,
                result.Abandoned);
        }

        return result;
    }

    /// <summary>
    /// Claims the next due message and delivers it, all inside one transaction.
    /// <para>
    /// The claim and the outcome share a transaction on purpose. If this process dies between the
    /// POST and the commit, the transaction rolls back, the message is due again and the receiver
    /// sees a duplicate — which the <c>processed_webhook_events</c> key makes harmless. That is
    /// the trade every at-least-once deliverer makes, chosen in the direction where a duplicate is
    /// cheap and a loss is not.
    /// </para>
    /// <para>
    /// Wrapped in the execution strategy because the context is configured with
    /// <c>EnableRetryOnFailure</c>, and a retrying strategy refuses a user-initiated transaction
    /// unless the whole transaction is handed to it — it has to be able to run the entire unit
    /// again. Each attempt therefore starts from a cleared change tracker and re-claims from
    /// scratch, so a retry cannot deliver against a stale claim.
    /// </para>
    /// </summary>
    private async Task<OutboxSweepStep> DeliverNextAsync(
        VelaCommerceDbContext db,
        Uri receiver,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async (CancellationToken token) =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var now = timeProvider.GetUtcNow();

                var message = await ClaimAsync(db, now, token);

                if (message is null)
                {
                    await transaction.RollbackAsync(token);
                    return OutboxSweepStep.Idle;
                }

                // THE BYTES, ONCE, FROM THE STORED TEXT. Everything about the signature depends on
                // this line being the only place a body is produced.
                var payload = Encoding.UTF8.GetBytes(message.Payload);

                // An explicit guard, narrow but real: UTF-8 round-trips every valid string, so the
                // only way this fails is a payload holding an unpaired surrogate, which encoding
                // would silently replace with U+FFFD and send under a signature computed over the
                // original. That is a corrupted row rather than a delivery problem, so it is
                // reported at Critical and the message is failed rather than transmitted — sending
                // it would spend the attempt and surface at the receiver as a forged signature.
                if (!string.Equals(Encoding.UTF8.GetString(payload), message.Payload, StringComparison.Ordinal))
                {
                    logger.LogCritical(
                        "Outbox message {MessageId} ({MessageType}) does not survive a UTF-8 round trip, so the "
                        + "bytes on the wire would not be the bytes that were signed. Not sending it.",
                        message.Id,
                        message.MessageType);

                    return await RecordFailureAsync(
                        db,
                        transaction,
                        message,
                        "The stored payload is not valid UTF-8 text; the transmitted bytes would not match the "
                        + "signature.",
                        now,
                        token);
                }

                var delivery = await deliveryClient.PostAsync(receiver, payload, message.SignatureHeader, token);

                if (!delivery.Success)
                {
                    // 429 and 5xx say "not now", not "this message is bad". Counting them toward
                    // MaxAttempts let anyone who could reach the public webhook route abandon real
                    // captured settlements: five rate-limited attempts inside the ~30s backoff
                    // window and the message is dead, which is the one outcome the outbox exists
                    // to prevent. Reschedule without spending the budget.
                    if (delivery.StatusCode is 429 or (>= 500 and <= 599))
                    {
                        return await RescheduleWithoutPenaltyAsync(
                            db, transaction, message, delivery.Error!, now, token);
                    }

                    return await RecordFailureAsync(db, transaction, message, delivery.Error!, now, token);
                }

                message.MarkDelivered(now);

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                logger.LogInformation(
                    "Delivered outbox message {MessageId} ({MessageType}) on attempt {Attempt}: {StatusCode}.",
                    message.Id,
                    message.MessageType,
                    message.Attempts,
                    delivery.StatusCode);

                return OutboxSweepStep.Delivered;
            },
            cancellationToken);
    }

    /// <summary>
    /// Takes the next due message and holds it for the length of this transaction.
    /// <para>
    /// Raw SQL, because this is a statement EF cannot express and must not rewrite.
    /// <c>FOR UPDATE SKIP LOCKED</c> is the claim; <c>LIMIT 1</c> is applied in the same query
    /// level as the lock, which is what makes "skip what another dispatcher holds and take the
    /// next one" true rather than "take the first row and then discover it is taken". The set is
    /// mapped, so the rows come back tracked and the outcome below is an ordinary
    /// <c>SaveChanges</c>.
    /// </para>
    /// <para>
    /// Nothing is composed onto the query — no <c>Where</c>, no <c>IgnoreQueryFilters</c>, and the
    /// entity carries no query filter of its own — so the SQL reaches PostgreSQL exactly as
    /// written. Composition would make EF wrap it in a subquery, and a locking clause buried in a
    /// subquery is how a queue quietly stops being a queue.
    /// </para>
    /// <para>
    /// The status is passed as a parameter rather than written as a literal so the enum stays the
    /// single source of that number, and the ordering matches
    /// <c>ix_outbox_messages_pending_deliver_after</c> so the claim is an index scan of exactly
    /// the rows that are due.
    /// </para>
    /// </summary>
    private static async Task<OutboxMessage?> ClaimAsync(
        VelaCommerceDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = (int)OutboxMessageStatus.Pending;

        var claimed = await db.Set<OutboxMessage>()
            .FromSql(
                $"""
                 SELECT *
                 FROM outbox_messages
                 WHERE status = {pending}
                   AND deliver_after <= {now}
                 ORDER BY deliver_after, id
                 LIMIT 1
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);

        return claimed.Count == 0 ? null : claimed[0];
    }

    /// <summary>
    /// Writes a failed attempt down and commits it. Committing the failure is the point: a rolled
    /// back failure is an attempt that never happened, and a message whose attempt count never
    /// rises is a message that is retried forever.
    /// </summary>
    /// <summary>
    /// Pushes a message back into the future without spending an attempt.
    /// <para>
    /// For answers that describe the receiver's state rather than the message's: 429 because
    /// the endpoint is rate limited, 5xx because it is unwell. Both are temporary and neither
    /// says anything is wrong with the payload, so letting them exhaust <c>MaxAttempts</c>
    /// would let an unauthenticated flood of the public webhook abandon real captured
    /// settlements permanently.
    /// </para>
    /// </summary>
    private async Task<OutboxSweepStep> RescheduleWithoutPenaltyAsync(
        VelaCommerceDbContext db,
        IDbContextTransaction transaction,
        OutboxMessage message,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        message.Defer(error, now + options.RetryDelayAfter(message.Attempts), now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning(
            "Outbox message {MessageId} was deferred without spending an attempt: {Error}",
            message.Id,
            error);

        return OutboxSweepStep.Retrying;
    }

    private async Task<OutboxSweepStep> RecordFailureAsync(
        VelaCommerceDbContext db,
        IDbContextTransaction transaction,
        OutboxMessage message,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        message.RecordFailure(error, now + options.RetryDelayAfter(message.Attempts), options.MaxAttempts, now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (message.Status is OutboxMessageStatus.Abandoned)
        {
            logger.LogError(
                "Outbox message {MessageId} ({MessageType}) was abandoned after {Attempts} attempts. Last error: "
                + "{Error}. The side effect it promised has not happened and will not happen without "
                + "intervention.",
                message.Id,
                message.MessageType,
                message.Attempts,
                message.LastError);

            return OutboxSweepStep.Abandoned;
        }

        logger.LogWarning(
            "Outbox message {MessageId} ({MessageType}) failed on attempt {Attempts}; retrying after "
            + "{DeliverAfter}. Error: {Error}.",
            message.Id,
            message.MessageType,
            message.Attempts,
            message.DeliverAfter,
            message.LastError);

        return OutboxSweepStep.Retrying;
    }
}

/// <summary>What one message's turn produced.</summary>
public enum OutboxSweepStep
{
    /// <summary>Nothing was due. The sweep stops here rather than spinning.</summary>
    Idle = 0,

    /// <summary>The receiver accepted it.</summary>
    Delivered = 1,

    /// <summary>It failed and is scheduled to be tried again.</summary>
    Retrying = 2,

    /// <summary>It failed for the last allowed time.</summary>
    Abandoned = 3,
}

/// <summary>
/// The tally for one sweep. Returned rather than logged only, so an integration test can assert on
/// what a sweep did without reading log output.
/// </summary>
/// <param name="Delivered">Messages the receiver accepted.</param>
/// <param name="Retrying">Messages that failed and will be tried again.</param>
/// <param name="Abandoned">Messages that failed for the last time.</param>
public readonly record struct OutboxSweepResult(int Delivered, int Retrying, int Abandoned)
{
    public static OutboxSweepResult Empty => default;

    /// <summary>Messages claimed and acted on, whatever the outcome.</summary>
    public int Attempted => Delivered + Retrying + Abandoned;

    /// <summary>Adds one message's outcome. <see cref="OutboxSweepStep.Idle"/> counts as nothing.</summary>
    public OutboxSweepResult With(OutboxSweepStep step) => step switch
    {
        OutboxSweepStep.Delivered => this with { Delivered = Delivered + 1 },
        OutboxSweepStep.Retrying => this with { Retrying = Retrying + 1 },
        OutboxSweepStep.Abandoned => this with { Abandoned = Abandoned + 1 },
        _ => this,
    };
}
