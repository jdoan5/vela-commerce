// THE ONE ENDPOINT AN ATTACKER CAN REACH WITHOUT A SESSION, AND THE FOUR THINGS THAT MAKE IT SAFE.
//
// 1. THE SIGNATURE IS VERIFIED OVER THE BYTES THAT ARRIVED, NEVER OVER A RE-SERIALIZATION.
//    The body is read as bytes before any model binding, and those exact bytes go to
//    PaymentSignature.Verify. Binding to a DTO and re-serializing produces different whitespace,
//    different property order and different number formatting, so the MAC would never match — and
//    the classic "fix" for that (verify the re-serialized form) silently stops verifying anything,
//    because the attacker controls what the DTO deserializes into. Deserialization happens AFTER
//    verification here, and only because the bytes have already been proved authentic.
//
// 2. EXACTLY-ONCE IS THE DATABASE'S JOB, NOT THIS FILE'S.
//    The processed_webhook_events insert and the order transition are ONE transaction. A duplicate
//    delivery loses on pk_processed_webhook_events and takes the transition down with it. There is
//    deliberately no "have I seen this event?" SELECT: two deliveries in flight would both find
//    nothing and both apply, which is the same race the checkout idempotency work closed once
//    already. The check and the effect are the same commit or they are not a guarantee.
//
// 3. ARRIVAL ORDER IS NOT TRUSTED, BECAUSE NOTHING ABOUT IT IS PROMISED.
//    OrderStateMachine has no Paid -> Paid edge and no backwards edge, so a replay, a late
//    payment.authorized after its own payment.succeeded, and a settlement for an order the reaper
//    already cancelled are all REFUSED BY CONSTRUCTION rather than by a status check somebody
//    remembered to write. This file asks the state machine whether the edge is legal instead of
//    letting Order.MarkPaid throw, and catches DomainException anyway as a backstop — an illegal
//    transition here is an ordinary event, and an ordinary event must never surface as a 500.
//
// 4. AN UNAUTHENTICATED REQUEST CANNOT REACH THE DATABASE AT ALL.
//    Order of work is load-bearing: rate limit, then bound the body, then verify, and only then
//    open a transaction. Everything before verification is one HMAC over at most 64 KiB, so the
//    cost of flooding this endpoint without the secret is bounded and constant. Nothing before
//    verification touches PostgreSQL, allocates per-order state, or answers with anything an
//    attacker can tune against.
//
// WHAT THIS ENDPOINT DELIBERATELY DOES NOT DO.
//
//    It does not release stock, cancel orders, or reconcile. A settlement that cannot be applied
//    is recorded and dropped; the reservation reaper owns expiry and the order state machine owns
//    legality. A receiver that started making judgement calls about money it could not apply would
//    be the second place in the system that decides an order's fate, and the two would disagree.

using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using VelaCommerce.Api.Contracts;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Messaging;
using VelaCommerce.Infrastructure.Payments;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Registration for the inbound settlement receiver: the endpoint a payment gateway posts to when
/// it has decided what happened to a deferred authorization.
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>
    /// The route the gateway posts to.
    /// <para>
    /// It must equal <see cref="OutboxOptions.DefaultReceiverPath"/>, which is where the outbox
    /// dispatcher posts when configuration names no explicit receiver — the one loose coupling in
    /// this phase, because the two halves were built by different hands and nothing in the type
    /// system joins them. Written as its own constant rather than as a reference to that one on
    /// purpose: the outbox's value is a *default* that a deployment may override through
    /// <c>Messaging:Outbox:ReceiverPath</c>, and a route that moved whenever somebody retuned the
    /// sender's default would be a far stranger failure than the mismatch it was meant to prevent.
    /// <see cref="MapWebhookEndpoints"/> compares the two at startup and says so loudly if they
    /// have drifted apart.
    /// </para>
    /// </summary>
    public const string SettlementRoute = "/api/payments/webhook";

    /// <summary>
    /// The most body this endpoint will read, in bytes.
    /// <para>
    /// A settlement event is nine short scalar fields — about 400 bytes on the wire — so 64 KiB is
    /// roughly a hundred and fifty times the largest legitimate payload and still small enough
    /// that a flood cannot make the process allocate anything interesting. It is a constant rather
    /// than configuration because it is a property of the payload format, not of a deployment: a
    /// body that needs more than this is not a settlement notification.
    /// </para>
    /// <para>
    /// The cap is enforced by reading, not only by trusting <c>Content-Length</c>. A chunked
    /// request carries no length at all, and a declared length is a claim rather than a fact.
    /// </para>
    /// </summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>Read buffer. One page, so a legitimate payload arrives in a single read.</summary>
    private const int ReadChunkBytes = 4 * 1024;

    /// <summary>
    /// Deliveries accepted per second, before any of them is verified.
    /// <para>
    /// The outbox dispatcher's own ceiling is ten per second (<c>BatchSize</c> 10 at a one-second
    /// <c>PollInterval</c>), so sixty leaves six times the headroom a legitimate sender can ever
    /// use while still bounding an anonymous flood to sixty HMACs a second. Configurable because a
    /// deployment that fans several senders into one receiver has a genuinely different number;
    /// see <see cref="RateLimitConfigurationKey"/>.
    /// </para>
    /// </summary>
    private const int DefaultPermitsPerSecond = 60;

    /// <summary>Configuration key for <see cref="DefaultPermitsPerSecond"/>. Colon-separated.</summary>
    private const string RateLimitConfigurationKey = "Payments:Webhook:MaxRequestsPerSecond";

    /// <summary>
    /// The authentication scheme named in <c>WWW-Authenticate</c> on a 401.
    /// <para>
    /// RFC 9110 §15.5.2 requires a 401 to carry that header, and a receiver that answers 401 with
    /// no challenge is a small protocol lie that costs nothing to avoid. The scheme name is
    /// deliberately uninformative — it names the mechanism, which is public in this repository,
    /// and nothing about why a particular request failed it.
    /// </para>
    /// </summary>
    private const string ChallengeScheme = "Vela-Signature realm=\"vela-payments\"";

    /// <summary>
    /// Log category. <c>ILogger&lt;T&gt;</c> is unavailable here because a static class cannot be a
    /// type argument, and inventing a marker type purely to satisfy the generic would be worse than
    /// naming the category once. Matches the convention in <see cref="CheckoutEndpoints"/>.
    /// </summary>
    private const string LogCategory = "VelaCommerce.Api.Endpoints.PaymentWebhook";

    /// <summary>
    /// Maps the settlement receiver. Called by the host, so this file never learns how the
    /// application is composed.
    /// <para>
    /// <b>This endpoint is in the public OpenAPI document on purpose</b>, which is the opposite of
    /// the usual instinct. Hiding it would buy nothing: the route, the payload type, the signature
    /// scheme and the shared development secret are all readable in this repository, so the only
    /// thing an omission could conceal from an attacker is the thing they already have. What
    /// publishing it buys is concrete. CI diffs the committed <c>openapi.json</c> against a
    /// freshly generated one, so the route above becomes a tripwire on the phase's one loose
    /// coupling — rename it and the build goes red instead of the settlements going quietly
    /// undelivered. The Demo Lab needs a described endpoint to point reviewers at. And a webhook
    /// receiver that nobody documents is how an integration ends up reverse-engineered from a
    /// packet capture. What is NOT published is any mapping from a failure to its cause: the
    /// description says the endpoint answers 400 and 401 without saying which check produces which.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.ServiceProvider;
        var configuration = services.GetService<IConfiguration>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger(LogCategory);

        WarnIfTheSenderPostsSomewhereElse(logger);

        // Created once per mapped host and captured by the filter below, rather than held in a
        // static field. A static limiter would be shared by every WebApplicationFactory in a test
        // process, so one test's burst would throttle the next test's first request — a flake that
        // reads as a webhook bug. The cost is one replenishment timer for the life of the host,
        // which is exactly as long as the endpoint it belongs to lives.
        var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = ReadPermitsPerSecond(configuration, logger),
            Window = TimeSpan.FromSeconds(1),

            // No queue. A settlement that waits in a queue is a row lock held open somewhere and a
            // dispatcher blocked behind it; an immediate 429 is retried with backoff by the very
            // sender that produced the burst, which is a better outcome than making it wait.
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

        // The limiter owns an auto-replenishment timer, so somebody has to own the limiter.
        // Tied to the host's lifetime: one per mapped host, disposed when that host stops,
        // which in a test process means one per WebApplicationFactory rather than one forever.
        app.ServiceProvider
            .GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping
            .Register(limiter.Dispose);

        app.MapPost(SettlementRoute, ReceivePaymentSettlementAsync)
            .WithTags("Payments")
            .WithName("ReceivePaymentSettlement")
            .WithSummary("Receive a signed settlement notification from the payment gateway")
            .WithDescription(
                "The inbound half of the payment integration. Every request must carry an "
                + "X-Vela-Signature header in the documented 't=<unix>,v1=<hex>' shape, computed "
                + "over the exact bytes of the body; the body is verified as transmitted and never "
                + "re-serialized, so a proxy that reformats JSON in flight will break the "
                + "signature. A verified event is deduplicated by its gateway-assigned id and "
                + "applied in one transaction, so delivering the same event twice moves the order "
                + "once and answers 200 both times. 200 is also the answer when the event cannot "
                + "be applied at all - the order is already Paid, or was cancelled, or no longer "
                + "exists - because none of those are the sender's fault and none of them are "
                + "fixed by retrying. 409 means the money in the event disagrees with the order "
                + "total and a human has to look. Requests that fail verification are answered "
                + "identically whatever went wrong with them.")
            .Accepts<PaymentSettlementEvent>("application/json")
            .Produces<PaymentSettlementAcknowledgement>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(async (context, next) =>
            {
                // FIRST filter in the pipeline, so the cheapest possible rejection happens before
                // the body is touched. Leases from a fixed-window limiter are not returned on
                // dispose - the window replenishes them - so holding one for the request's
                // lifetime costs nothing and keeps the using-block honest.
                using var lease = await limiter.AcquireAsync(
                    permitCount: 1,
                    context.HttpContext.RequestAborted);

                if (!lease.IsAcquired)
                {
                    var response = context.HttpContext.Response;
                    response.Headers.RetryAfter =
                        lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
                            ? ((int)Math.Ceiling(after.TotalSeconds)).ToString(
                                CultureInfo.InvariantCulture)
                            : "1";

                    return TooManyProblem();
                }

                return await next(context);
            });

        return app;
    }

    /// <summary>
    /// Verifies, deduplicates and applies one settlement notification.
    /// <para>
    /// Takes <see cref="HttpContext"/> rather than a bound payload, which is the whole point: a
    /// bound parameter would mean the framework had already read and re-shaped the body, and the
    /// signature covers the bytes it read. Nothing here binds anything until
    /// <see cref="PaymentSignature.Verify"/> has said the bytes are ours.
    /// </para>
    /// </summary>
    private static async Task<IResult> ReceivePaymentSettlementAsync(
        HttpContext http,
        VelaCommerceDbContext db,
        PaymentSimulatorOptions options,
        TimeProvider timeProvider,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // A settlement notification is a money path, so the committed development secret is
        // refused here exactly as it is on the authorization path. Checked before the body is read
        // rather than at startup, because startup also happens under the build-time OpenAPI
        // generator, which runs Program.cs as Production and would turn a deployment safeguard
        // into a broken build.
        try
        {
            options.AssertUsable(environment.IsDevelopment());
        }
        catch (InvalidOperationException exception)
        {
            logger.LogCritical(
                exception,
                "A settlement notification arrived but the signing secret is unusable, so nothing "
                + "can be verified. Every delivery will be refused until this is configured.");

            return NotConfiguredProblem();
        }

        // Nothing sensible caches a settlement receipt, and a shared cache in front of this
        // endpoint replaying a 200 would look exactly like a successful delivery.
        http.Response.Headers.CacheControl = "no-store";

        var body = await ReadRawBodyAsync(http.Request, cancellationToken);

        if (body is null)
        {
            logger.LogWarning(
                "Refused a settlement notification whose body exceeded {MaxBodyBytes} bytes.",
                MaxBodyBytes);

            return PayloadTooLargeProblem();
        }

        // Exactly one header value. Two X-Vela-Signature headers arrive here joined by a comma,
        // which the parser would read as one header carrying two fields - and a scheme whose
        // fields are labelled is a scheme where an attacker gets to pick which v1= is checked.
        var supplied = http.Request.Headers[PaymentSignature.HeaderName];
        var headerValue = supplied.Count == 1 ? supplied[0] : null;

        var now = timeProvider.GetUtcNow();

        var verification = PaymentSignature.Verify(
            body,
            headerValue,
            options.SigningSecret,
            now,
            options.SignatureTolerance);

        if (verification is not PaymentSignatureResult.Valid)
        {
            return RefuseUnverified(http, verification, logger);
        }

        // PAST THIS LINE THE CALLER HAS PROVED IT HOLDS THE SHARED SECRET.
        // Errors may now be specific, because the only party that can read them is the party that
        // signed the request, and a sender debugging its own payload needs to know what was wrong.

        PaymentSettlementEvent? settlement;

        try
        {
            settlement = JsonSerializer.Deserialize<PaymentSettlementEvent>(
                body,
                PaymentSettlementEvent.SerializerOptions);
        }
        catch (JsonException exception)
        {
            logger.LogError(
                exception,
                "A correctly signed settlement notification could not be read as a settlement "
                + "event. The sender and the receiver disagree about the payload shape.");

            return UnreadablePayloadProblem(exception.Message);
        }

        if (settlement is null)
        {
            return UnreadablePayloadProblem("The body deserialized to null.");
        }

        if (Malformed(settlement) is { } complaint)
        {
            logger.LogError(
                "A correctly signed settlement notification was rejected: {Complaint}", complaint);

            return UnreadablePayloadProblem(complaint);
        }

        // Normalized so the lookup compares against the canonical string the column holds, and so
        // a reference that cannot possibly name an order costs no round trip.
        if (!OrderNumbers.TryNormalize(settlement.OrderReference, out var orderNumber))
        {
            logger.LogError(
                "Settlement event {EventId} names order reference '{OrderReference}', which is not "
                + "an order number this store could ever have issued.",
                settlement.EventId,
                settlement.OrderReference);

            return UnreadablePayloadProblem(
                $"'{settlement.OrderReference}' is not a well-formed order number.");
        }

        // THE EVENT TYPES, AND WHY ONLY ONE OF THEM MOVES AN ORDER.
        //
        //   payment.succeeded - the gateway has taken the money. This is the only event that pays
        //                       an order, and the only one that needs the amount to agree.
        //   payment.authorized - the funds are reserved and have not moved. Recording it is the
        //                       correct handling: there is no Pending -> Pending edge and there
        //                       should not be one, because an order that "became" Pending twice is
        //                       indistinguishable from a replay. In the Reorder scenario this
        //                       event arrives AFTER the capture it logically preceded, so anything
        //                       that acted on it would be undoing a completed payment.
        //   anything else     - acknowledged and dropped, WITHOUT a dedupe row. Recording a
        //                       processed-event id for something this build cannot process would
        //                       be a lie that permanently blocks a redelivery after support for it
        //                       ships. 200 rather than 400 because a sender cannot fix its own
        //                       vocabulary by retrying, and a receiver that 4xx'd every unfamiliar
        //                       event would make adding one a breaking change.
        if (settlement.EventType is not (PaymentSettlementEvent.SucceededType
            or PaymentSettlementEvent.AuthorizedType))
        {
            logger.LogWarning(
                "Settlement event {EventId} for order {OrderNumber} is of type {EventType}, which "
                + "this receiver has no handling for. Acknowledged and dropped, and deliberately "
                + "NOT recorded as processed.",
                settlement.EventId,
                orderNumber,
                settlement.EventType);

            return TypedResults.Ok(new PaymentSettlementAcknowledgement(
                settlement.EventId, "unsupported-event-type", Applied: false, orderNumber, null));
        }

        Money? captured = null;

        if (settlement.EventType is PaymentSettlementEvent.SucceededType)
        {
            try
            {
                // Reassembled from minor units plus an ISO code, which is what crossed the wire.
                // Built out here rather than inside the transaction so a malformed currency is a
                // 400 about a payload instead of a rolled-back transaction about an order.
                captured = new Money(settlement.Amount, settlement.Currency);
            }
            catch (DomainException exception)
            {
                return UnreadablePayloadProblem(exception.Message);
            }
        }

        SettlementResult result;

        try
        {
            result = await ApplySettlementAsync(
                db, settlement, orderNumber, captured, now, logger, cancellationToken);
        }
        catch (DomainException exception)
        {
            // BACKSTOP, NOT THE MECHANISM. Every illegal transition this endpoint can foresee is
            // asked about before it is attempted, so reaching here means the domain refused
            // something the receiver believed was legal - a genuine disagreement between the two,
            // and the one case that must never be answered with a bare 500 by the exception
            // handler. 409 rather than 200: nothing was applied, and a silent success would hide
            // a real inconsistency behind a green delivery log.
            logger.LogCritical(
                exception,
                "Settlement event {EventId} for order {OrderNumber} was refused by the domain "
                + "after the receiver had judged it legal. Nothing was applied.",
                settlement.EventId,
                orderNumber);

            return CannotApplyProblem(orderNumber, exception.Message);
        }

        return Describe(settlement, result, logger);
    }

    /// <summary>
    /// The transaction that makes duplicate delivery harmless: insert the event id and apply the
    /// transition together, or do neither.
    /// <para>
    /// <b>There is no "have I seen this event?" query, and adding one would be a regression.</b>
    /// Two deliveries in flight would both find nothing, both decide to apply, and both proceed —
    /// the dedupe would hold only for deliveries far enough apart not to need it. The insert is
    /// therefore attempted unconditionally and <c>pk_processed_webhook_events</c> picks the
    /// winner; the loser's <see cref="DbUpdateException"/> is not an error but the answer.
    /// </para>
    /// <para>
    /// <b>Reads ignore the tenancy filter, and must.</b> <c>DemoTenancy</c> fails closed: with no
    /// visitor bound it matches nothing rather than everything, which is the only safe direction
    /// for it to fail in and is exactly wrong here. A gateway has no session cookie, so an
    /// unfiltered read on this path would find no order, ever, and would do so silently — every
    /// settlement answering "order-not-found" while the orders sat right there. This is the same
    /// trap <c>ReservationReaper</c> documents, and it is invisible in a unit test with no filter
    /// in the model.
    /// </para>
    /// </summary>
    private static async Task<SettlementResult> ApplySettlementAsync(
        VelaCommerceDbContext db,
        PaymentSettlementEvent settlement,
        string orderNumber,
        Money? captured,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async (CancellationToken token) =>
            {
                // The execution strategy may run this lambda again after a transient fault, and a
                // second run must not inherit the first run's tracked entities - including the
                // ProcessedWebhookEvent whose insert may be exactly what failed.
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                // Added FIRST, so that every path which commits below carries the dedupe row with
                // it. The row records "this delivery was handled", which is true whether handling
                // meant paying the order or deciding there was nothing to do.
                db.Set<ProcessedWebhookEvent>().Add(new ProcessedWebhookEvent(
                    settlement.EventId,
                    now,
                    settlement.EventType,
                    orderNumber));

                // TAKE THE ROW UNDER LOCK BEFORE READING IT.
                //
                // The reservation reaper writes the same order. An adversarial review reproduced
                // both interleavings every run: the reaper reading Pending, this settlement
                // paying, and the reaper's blind write turning a captured payment into a
                // Cancelled order; and the mirror, where this settlement paid an order whose
                // reservations the reaper had just released, so the timeline shipped it having
                // moved zero units and the stock went back on sale already sold. No entity
                // carries a concurrency token, so both writers won simply by being last.
                //
                // FOR UPDATE, not SKIP LOCKED — this transaction must WAIT for the reaper rather
                // than skip the row and silently do nothing. Locked by id first because Include
                // cannot compose onto FromSql; the read below then sees the row this transaction
                // holds.
                _ = await db.Database
                    .SqlQuery<Guid>(
                        $"""
                         SELECT id
                         FROM orders
                         WHERE order_number = {orderNumber}
                           AND deleted_at IS NULL
                         FOR UPDATE
                         """)
                    .ToListAsync(token);

                var order = await db.Orders
                    .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                    .Include(entity => entity.Lines)
                    .FirstOrDefaultAsync(entity => entity.OrderNumber == orderNumber, token);

                var outcome = SettlementOutcome.Acknowledged;

                if (order is null)
                {
                    // AN UNKNOWN ORDER IS A 200 AND A DROPPED EVENT, NOT A 404. THE ARGUMENT:
                    //
                    // A 404 tells the sender "retry, it may show up". In this system it never
                    // will. The notification was enqueued by the SAME SaveChangesAsync that
                    // committed the order's state, so an outbox row cannot become visible before
                    // its order row does — the arrival-before-insert race that makes 404 the right
                    // answer for a real gateway is structurally impossible here. What remains as a
                    // cause is the demo reset wiping the orders table while undelivered
                    // notifications are still in flight, and for that a 404 is actively harmful:
                    // the dispatcher would burn five attempts and abandon the message with an
                    // alarming LastError, on every reset, for something nobody did wrong.
                    //
                    // The dedupe row is still written. It is the honest record — this delivery WAS
                    // handled, and the handling was to drop it — and it makes the inevitable second
                    // delivery of the same event cost one failed insert instead of another lookup.
                    outcome = SettlementOutcome.OrderNotFound;

                    logger.LogWarning(
                        "Settlement event {EventId} names order {OrderNumber}, which does not "
                        + "exist. Recorded and dropped; retrying cannot help. If this is not a "
                        + "demo reset, the tenancy filter is the first thing to suspect.",
                        settlement.EventId,
                        orderNumber);
                }
                else if (settlement.EventType is PaymentSettlementEvent.AuthorizedType)
                {
                    // Funds reserved, nothing moved. Recorded, and the order stays exactly where it
                    // is. See the note at the call site for why acting on this would be wrong.
                    logger.LogInformation(
                        "Settlement event {EventId} authorized order {OrderNumber} (sequence "
                        + "{Sequence}); it stays {Status} until a capture arrives.",
                        settlement.EventId,
                        orderNumber,
                        settlement.Sequence,
                        order.Status);
                }
                else if (!OrderStateMachine.IsLegal(order.Status, OrderStatus.Paid))
                {
                    // THE STATE MACHINE, NOT A STATUS CHECK, IS THE AUTHORITY.
                    //
                    // Asking it covers every case at once and keeps working when the edge table
                    // changes: an order already Paid (a duplicate that outran the ledger, or the
                    // Reorder scenario's capture landing twice), one the reaper cancelled while the
                    // settlement was in flight, one already Packed or Shipped. There is no
                    // Paid -> Paid edge precisely so that this is detectable rather than idempotent
                    // by accident.
                    outcome = SettlementOutcome.NoLegalTransition;

                    logger.LogInformation(
                        "Settlement event {EventId} would have paid order {OrderNumber}, which is "
                        + "{Status}. No legal transition to Paid; recorded and dropped.",
                        settlement.EventId,
                        orderNumber,
                        order.Status);
                }
                else if (captured != order.Total)
                {
                    // THE ONE CASE WORTH REFUSING TO RECORD.
                    //
                    // Order.MarkPaid would throw here anyway, but a caught exception is a worse
                    // answer than a decision: this is a signed, authentic event whose money
                    // disagrees with ours, which is either a repricing bug or the beginning of a
                    // real incident. The whole transaction rolls back, so NO dedupe row is written
                    // and a corrected redelivery of the same event id can still be applied. The
                    // sender's retries and the eventual abandoned outbox row are the paper trail.
                    logger.LogCritical(
                        "Settlement event {EventId} says {Captured} was captured for order "
                        + "{OrderNumber}, whose total is {Total}. Nothing applied and nothing "
                        + "recorded; this needs a human.",
                        settlement.EventId,
                        captured,
                        orderNumber,
                        order.Total);

                    await transaction.RollbackAsync(token);

                    return new SettlementResult(
                        SettlementOutcome.AmountMismatch, orderNumber, order.Status);
                }
                else
                {
                    // MarkPaid refuses a capture that does not equal the total to the cent, which
                    // the branch above has already established, and refuses an illegal transition,
                    // which the branch before it has. Both are asked rather than caught so that the
                    // exception path stays what it should be: unreachable.
                    order.MarkPaid(captured.Value, settlement.GatewayReference, now);

                    // CONFIRMING THE RESERVATIONS IS NOT OPTIONAL, AND FORGETTING IT OVERSELLS.
                    //
                    // Checkout leaves them Held when the gateway defers, because nothing is
                    // promised yet. ReservationReaper releases Held reservations once their window
                    // closes and only cancels orders still Pending - so a paid order whose
                    // reservations were left Held would have its units handed back to the pool
                    // fifteen minutes later while the order stayed Paid. That is an oversell with
                    // no error anywhere. The units stay reserved rather than being deducted:
                    // on_hand only drops when the parcel ships.
                    var held = await db.StockReservations
                        .Where(entity => entity.OrderId == order.Id
                                         && entity.Status == ReservationStatus.Held)
                        .ToListAsync(token);

                    foreach (var reservation in held)
                    {
                        reservation.Confirm();
                    }

                    outcome = SettlementOutcome.Settled;

                    logger.LogInformation(
                        "Settlement event {EventId} paid order {OrderNumber} with {Captured} and "
                        + "confirmed {Reservations} reservation(s).",
                        settlement.EventId,
                        orderNumber,
                        captured,
                        held.Count);
                }

                try
                {
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                }
                catch (DbUpdateException exception) when (IsDuplicateDelivery(exception))
                {
                    // THE DUPLICATE LOST ON THE PRIMARY KEY, AND WITH IT WENT THE TRANSITION.
                    // This is the mechanism working, not a failure: whatever this delivery was
                    // about to do to the order was rolled back by the same statement that caught
                    // it, so the order cannot advance twice however many copies arrive.
                    await transaction.RollbackAsync(token);

                    logger.LogInformation(
                        "Settlement event {EventId} for order {OrderNumber} has already been "
                        + "processed. The duplicate lost on the primary key and nothing was "
                        + "applied a second time.",
                        settlement.EventId,
                        orderNumber);

                    // Read the order back so the sender is told what it already is. Cheap, and it
                    // turns "duplicate" into the far more useful "duplicate, still Paid once".
                    db.ChangeTracker.Clear();

                    var settled = await db.Orders
                        .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                        .Where(entity => entity.OrderNumber == orderNumber)
                        .Select(entity => (OrderStatus?)entity.Status)
                        .FirstOrDefaultAsync(token);

                    return new SettlementResult(SettlementOutcome.Duplicate, orderNumber, settled);
                }

                return new SettlementResult(outcome, orderNumber, order?.Status);
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads the body as the bytes that arrived, capped, and returns <see langword="null"/> when
    /// the cap is exceeded.
    /// <para>
    /// Called before anything binds a model, because <see cref="PaymentSignature"/> verifies the
    /// transmitted bytes and a re-serialization is a different message. <c>Content-Length</c> is
    /// consulted as a cheap early out and then not trusted: it is absent on a chunked request and
    /// is in any case a claim by the sender.
    /// </para>
    /// </summary>
    private static async Task<byte[]?> ReadRawBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var chunk = new byte[ReadChunkBytes];
        using var body = new MemoryStream(capacity: ReadChunkBytes);

        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (body.Length + read > MaxBodyBytes)
            {
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    /// <summary>
    /// Answers a request whose signature did not verify, telling the sender whether to give up
    /// (400) or to fix its credential (401), and telling it nothing else.
    /// <para>
    /// <b>The three failures share one body, one set of headers and one wording.</b> The status
    /// code differs because it is the sender's retry instruction and both values mean "do not
    /// retry this request as it stands"; the response says which of those two it is and never
    /// which check produced it. Probing therefore reveals only what the repository already
    /// documents — that there is a header, and that it is signed.
    /// </para>
    /// <para>
    /// <b>Expired is a 400, not a 408.</b> RFC 9110 §15.5.9 defines 408 as the server giving up
    /// waiting for a request, and it explicitly invites the client to repeat the request
    /// unmodified — which is the one thing that cannot possibly help, since an identical replay is
    /// just as far outside the window. The honest reading is that the request that arrived is not
    /// acceptable, which is 400. It is also logged at Warning rather than Information: a
    /// well-formed signature outside its window is either a signature lifted from a log and
    /// replayed, or a clock that has drifted, and both are worth noticing.
    /// </para>
    /// </summary>
    private static IResult RefuseUnverified(
        HttpContext http,
        PaymentSignatureResult verification,
        ILogger logger)
    {
        // The supplied signature is never logged, at any level. It is an oracle for anyone probing
        // the endpoint, and a log that anybody can read is not the place to keep one.
        logger.Log(
            verification is PaymentSignatureResult.Expired
                ? LogLevel.Warning
                : LogLevel.Information,
            "Refused an unverified settlement notification ({Verification}) from {RemoteAddress}.",
            verification,
            http.Connection.RemoteIpAddress);

        if (verification is PaymentSignatureResult.Mismatched)
        {
            http.Response.Headers.WWWAuthenticate = ChallengeScheme;

            return UnverifiedProblem(StatusCodes.Status401Unauthorized);
        }

        return UnverifiedProblem(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Turns the applied outcome into a 200 the sender can stop retrying on, or the single 409
    /// that says a human has to look.
    /// </summary>
    private static IResult Describe(
        PaymentSettlementEvent settlement,
        SettlementResult result,
        ILogger logger)
    {
        if (result.Outcome is SettlementOutcome.AmountMismatch)
        {
            return CannotApplyProblem(
                result.OrderNumber,
                "The captured amount in the notification does not equal the order total.");
        }

        var outcome = result.Outcome switch
        {
            SettlementOutcome.Settled => "settled",
            SettlementOutcome.Duplicate => "duplicate",
            SettlementOutcome.NoLegalTransition => "no-legal-transition",
            SettlementOutcome.OrderNotFound => "order-not-found",
            _ => "acknowledged",
        };

        logger.LogDebug(
            "Settlement event {EventId} answered {Outcome}.", settlement.EventId, outcome);

        return TypedResults.Ok(new PaymentSettlementAcknowledgement(
            settlement.EventId,
            outcome,
            Applied: result.Outcome is SettlementOutcome.Settled,
            result.OrderNumber,
            result.Status?.ToString()));
    }

    /// <summary>
    /// True when the failed insert was this event id arriving for the second time.
    /// <para>
    /// The provider-specific half of the mechanism, and it belongs beside
    /// <c>CheckoutConflicts</c> in Infrastructure rather than here — that type exists precisely so
    /// the endpoints do not have to know PostgreSQL exists, and it already reads SQLSTATE and
    /// constraint names for the checkout's two unique indexes. It is inlined here only because
    /// this slice does not own that file; moving it is a two-line change and the right one.
    /// </para>
    /// <para>
    /// Matched on the constraint name as well as the SQLSTATE, so an unrelated unique violation in
    /// the same transaction cannot be mistaken for a duplicate delivery and quietly answered 200.
    /// The name is repeated as a literal for the same reason <c>CheckoutConflicts</c> repeats its
    /// own: matching a constraint by name is a runtime contract with the database, and pointing it
    /// at a constant in the mapping would let a rename turn a recognised duplicate into an
    /// unhandled 500 with nothing failing to compile.
    /// </para>
    /// </summary>
    private static bool IsDuplicateDelivery(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" } postgres
        && string.Equals(
            postgres.ConstraintName, "pk_processed_webhook_events", StringComparison.Ordinal);

    /// <summary>
    /// Checks the fields the database and the domain will insist on, before either of them is
    /// asked. Returns a complaint, or <see langword="null"/> when the payload is usable.
    /// <para>
    /// The lengths are not arbitrary: they are the column widths in
    /// <c>processed_webhook_events</c>. Catching an over-long id here turns what would be a
    /// truncation or a provider exception mid-transaction into a plain 400 naming the field.
    /// </para>
    /// </summary>
    private static string? Malformed(PaymentSettlementEvent settlement)
    {
        if (string.IsNullOrWhiteSpace(settlement.EventId))
        {
            return "eventId is required; it is the dedupe key and nothing can be recorded without it.";
        }

        if (settlement.EventId.Length > ProcessedWebhookEvent.MaxEventIdLength)
        {
            return $"eventId is longer than {ProcessedWebhookEvent.MaxEventIdLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(settlement.EventType) || settlement.EventType.Length > 64)
        {
            return "eventType is required and must be at most 64 characters.";
        }

        return null;
    }

    /// <summary>
    /// Reads the per-second delivery allowance, falling back to the default for anything absent or
    /// unusable. Hand-bound, matching <c>PaymentSimulatorOptions</c> and <c>OutboxOptions</c>.
    /// <para>
    /// Deliberately does not throw on a bad value. Mapping happens during build-time OpenAPI
    /// generation as well as at startup, and refusing to compose the host over a rate limit would
    /// break the build to protect a number that has a perfectly good default.
    /// </para>
    /// </summary>
    private static int ReadPermitsPerSecond(IConfiguration? configuration, ILogger? logger)
    {
        var configured = configuration?[RateLimitConfigurationKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultPermitsPerSecond;
        }

        if (int.TryParse(
                configured,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var permits)
            && permits > 0)
        {
            return permits;
        }

        logger?.LogWarning(
            "{Key} is '{Value}', which is not a positive whole number. Falling back to "
            + "{Default} deliveries per second.",
            RateLimitConfigurationKey,
            configured,
            DefaultPermitsPerSecond);

        return DefaultPermitsPerSecond;
    }

    /// <summary>
    /// Says so at startup if the sender's default path and this receiver's route have drifted
    /// apart. A warning rather than a throw: the host also composes under the build-time OpenAPI
    /// generator, and a mismatched constant is a misconfiguration to fix, not a reason to fail a
    /// build.
    /// </summary>
    private static void WarnIfTheSenderPostsSomewhereElse(ILogger? logger)
    {
        if (string.Equals(SettlementRoute, OutboxOptions.DefaultReceiverPath, StringComparison.Ordinal))
        {
            return;
        }

        logger?.LogWarning(
            "The settlement receiver is mapped at {Route} but the outbox dispatcher posts to "
            + "{DefaultReceiverPath} unless configuration says otherwise. Settlements will be "
            + "delivered nowhere until {Key} names the route above.",
            SettlementRoute,
            OutboxOptions.DefaultReceiverPath,
            $"{OutboxOptions.SectionName}:ReceiverPath");
    }

    /// <summary>
    /// The single answer for every unverified request. One title, one detail, whatever went wrong.
    /// </summary>
    private static IResult UnverifiedProblem(int statusCode) =>
        TypedResults.Problem(
            title: "The settlement notification could not be verified",
            detail: "This endpoint accepts signed notifications only. The request must carry an "
                    + "X-Vela-Signature header and a body that is byte-for-byte what was signed. "
                    + "Nothing has been recorded and no order has changed.",
            statusCode: statusCode);

    private static IResult PayloadTooLargeProblem() =>
        TypedResults.Problem(
            title: "That body is too large to be a settlement notification",
            detail: $"The receiver reads at most {MaxBodyBytes} bytes. A settlement event is a few "
                    + "hundred; nothing legitimate approaches this limit.",
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult TooManyProblem() =>
        TypedResults.Problem(
            title: "Too many settlement notifications",
            detail: "This receiver is rate limited. Nothing was read, verified or recorded. Retry "
                    + "after the interval in the Retry-After header; a sender with a backoff will "
                    + "not lose the notification.",
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// A signed request whose body this build cannot make sense of. Specific on purpose: the
    /// caller has already proved it holds the secret, so it is our own sender misbehaving and it
    /// needs to know how.
    /// </summary>
    private static IResult UnreadablePayloadProblem(string detail) =>
        TypedResults.Problem(
            title: "The settlement notification was signed but could not be read",
            detail: detail + " The signature verified, so this is a disagreement about the payload "
                    + "rather than a security problem. Nothing has been recorded.",
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult CannotApplyProblem(string? orderNumber, string detail) =>
        TypedResults.Problem(
            title: "The settlement could not be applied to the order",
            detail: detail + " Nothing was recorded and the order is unchanged, so a corrected "
                    + "redelivery of this same event will still be accepted. This has been logged "
                    + "at Critical.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["orderNumber"] = orderNumber });

    private static IResult NotConfiguredProblem() =>
        TypedResults.Problem(
            title: "Settlement notifications cannot be verified",
            detail: "The receiver is not configured with a usable signing secret, so no "
                    + "notification can be authenticated. This has been logged at Critical.",
            statusCode: StatusCodes.Status500InternalServerError);

    /// <summary>What one delivery turned out to be. Every value but the last is a 200.</summary>
    private enum SettlementOutcome
    {
        /// <summary>This delivery paid the order. The only outcome that reports <c>applied</c>.</summary>
        Settled,

        /// <summary>The event id was already recorded; the insert lost on the primary key.</summary>
        Duplicate,

        /// <summary>
        /// The order exists but cannot legally become Paid — already Paid, cancelled, or further
        /// along. Recorded so the next copy is cheap.
        /// </summary>
        NoLegalTransition,

        /// <summary>
        /// A recognised event with nothing to do: <c>payment.authorized</c>, which reserves funds
        /// without moving them.
        /// </summary>
        Acknowledged,

        /// <summary>No order with that number. Recorded and dropped; see the reasoning at the site.</summary>
        OrderNotFound,

        /// <summary>
        /// The money in the event does not equal the order total. The only outcome that rolls back
        /// without recording, and the only one that is not a 200.
        /// </summary>
        AmountMismatch,
    }

    /// <summary>
    /// The transaction's answer. <paramref name="Status"/> is the order's status as at commit, or
    /// <see langword="null"/> when there was no order to read one from.
    /// </summary>
    private sealed record SettlementResult(
        SettlementOutcome Outcome,
        string? OrderNumber,
        OrderStatus? Status);
}
