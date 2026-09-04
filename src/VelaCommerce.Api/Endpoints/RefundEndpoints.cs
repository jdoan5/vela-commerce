using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using VelaCommerce.Api.Contracts;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Domain.Payments;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Giving money back, and cancelling an order that has already taken some.
/// <para>
/// <b>Why this surface exists at all.</b> The order aggregate could refund from the first commit of
/// this repository and nothing ever called it, which made the capability a claim rather than a
/// feature. Worse, the state machine's <c>Paid -&gt; Cancelled</c> edge was reachable while
/// refunding a cancelled order was not, so taking that edge produced an order holding money that
/// no code path could return — a test named that gap deliberately rather than papering over it.
/// This file closes it.
/// </para>
/// <para>
/// <b>Both handlers write in the same order, and the order is the design: ask the gateway, then
/// record.</b> A ledger row here means money actually moved, so it is written only after the
/// gateway confirms. The inverse — record then call — is the arrangement that survives every happy
/// path in testing and then, the first time an acquirer refuses, tells a shopper their money is on
/// the way when it is not. The simulator can be made to refuse on demand precisely so that this
/// ordering is exercised rather than asserted.
/// </para>
/// </summary>
public static class RefundEndpoints
{
    /// <summary>
    /// The conventional header (IETF <c>draft-ietf-httpapi-idempotency-key-header</c>), accepted
    /// alongside the body field exactly as the checkout accepts it.
    /// </summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// Log category. <c>ILogger&lt;T&gt;</c> is unavailable because a static class cannot be a type
    /// argument, and inventing a marker type to satisfy the generic would be worse than naming the
    /// category once.
    /// </summary>
    private const string LogCategory = "VelaCommerce.Api.Endpoints.Refunds";

    public static IEndpointRouteBuilder MapRefundEndpoints(this IEndpointRouteBuilder app)
    {
        var refunds = app
            .MapGroup("/api")
            .WithTags("Refunds")
            .AddEndpointFilter(PreventSharedCachingAsync);

        refunds.MapPost("/orders/{orderNumber}/refunds", RefundOrderAsync)
            .WithName("RefundOrder")
            .WithSummary("Return some or all of what an order captured")
            .WithDescription(
                "Refunds against the payment the order was settled by, not against the order, "
                + "which is why an unpaid order has nothing to refund. Omit the amount for the "
                + "whole outstanding balance. The idempotency key is required and is what makes a "
                + "retry safe: the same key returns the first refund with replayed=true and issues "
                + "no second one. "
                + "409 means the order is in a status that cannot be refunded, or the amount is "
                + "more than is left, or the gateway refused - in every case nothing was recorded "
                + "and no money moved. 502 means the gateway could not be reached at all, which is "
                + "worth retrying with the same key. "
                + "Unlike GET /api/orders/{orderNumber}, the signed retrieval token does NOT open "
                + "this endpoint: a link that lets somebody read a receipt must not also let them "
                + "move the money.")
            .Produces<RefundResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        refunds.MapPost("/orders/{orderNumber}/cancellation", CancelOrderAsync)
            .WithName("CancelOrder")
            .WithSummary("Cancel an order, returning everything it has taken")
            .WithDescription(
                "Cancels and refunds as one act, so the two can never come apart. A Pending order "
                + "captured nothing and is simply cancelled; a Paid order has its full outstanding "
                + "balance returned first, and only then is cancelled. Either way the units it was "
                + "holding go back on the shelf. "
                + "Packed and Shipped orders are refused with 409 because the parcel has moved - "
                + "refund those with POST /api/orders/{orderNumber}/refunds instead, which leaves "
                + "the fulfilment status telling the truth about where the goods are. "
                + "Cancelling an already-cancelled order replays rather than failing.")
            .Produces<RefundResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    /// <summary>
    /// Marks every response here uncacheable, for the same reason the checkout group does: these
    /// bodies carry one visitor's money.
    /// </summary>
    private static async ValueTask<object?> PreventSharedCachingAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        headers.Pragma = "no-cache";

        return await next(context);
    }

    private static async Task<Results<Ok<RefundResponse>, ProblemHttpResult>> RefundOrderAsync(
        string orderNumber,
        RefundRequest? request,
        HttpContext http,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        IPaymentGateway gateway,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        if (!OrderNumbers.TryNormalize(orderNumber, out var normalized))
        {
            return OrderNotFoundProblem();
        }

        if (!TryReadIdempotencyKey(request?.IdempotencyKey, http, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem;
        }

        if (request?.Amount is <= 0)
        {
            return TypedResults.Problem(
                title: "That refund amount cannot be accepted",
                detail: "A refund must be for a positive number of minor units. Omit the amount "
                        + "entirely to refund the whole outstanding balance.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await ApplyAsync(
            db,
            session,
            gateway,
            timeProvider,
            logger,
            normalized,
            idempotencyKey,
            request?.ScenarioHint,
            cancelling: false,
            requestedAmount: request?.Amount,
            cancellationToken);
    }

    private static async Task<Results<Ok<RefundResponse>, ProblemHttpResult>> CancelOrderAsync(
        string orderNumber,
        CancelOrderRequest? request,
        HttpContext http,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        IPaymentGateway gateway,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        if (!OrderNumbers.TryNormalize(orderNumber, out var normalized))
        {
            return OrderNotFoundProblem();
        }

        if (!TryReadIdempotencyKey(request?.IdempotencyKey, http, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem;
        }

        return await ApplyAsync(
            db,
            session,
            gateway,
            timeProvider,
            logger,
            normalized,
            idempotencyKey,
            request?.ScenarioHint,
            cancelling: true,
            requestedAmount: null,
            cancellationToken);
    }

    /// <summary>
    /// The whole of both operations, because they differ by three decisions and share everything
    /// else: whether the order is also cancelled, whether stock comes back, and how much moves.
    /// Two copies of this would drift, and the half that drifted would be the money.
    /// </summary>
    private static async Task<Results<Ok<RefundResponse>, ProblemHttpResult>> ApplyAsync(
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        IPaymentGateway gateway,
        TimeProvider timeProvider,
        ILogger logger,
        string orderNumber,
        string idempotencyKey,
        string? scenarioHint,
        bool cancelling,
        long? requestedAmount,
        CancellationToken cancellationToken)
    {
        // NO SESSION MEANS NO ORDER, AND THIS IS CHECKED HERE RATHER THAN LEFT TO THE FILTER.
        //
        // The claim below runs with query filters suppressed, because EF wraps a filtered FromSql
        // in a subquery and the FOR UPDATE goes with it - the same trap ReservationReaper documents.
        // Suppressing the filter means the predicate that normally guarantees tenancy is not there,
        // so the session id is a parameter of the SQL instead. A null session must therefore be
        // refused before the query, not after: without this, "demo_session_id = NULL" would match
        // nothing today and would be one careless edit away from matching everything.
        if (session.SessionId is not { } sessionId)
        {
            return OrderNotFoundProblem();
        }

        var strategy = db.Database.CreateExecutionStrategy();

        // The lambda's return type is spelled out because both TypedResults.Ok and
        // TypedResults.Problem convert implicitly to the union, so inference cannot pick between
        // them and silently falls through to the void-returning overload.
        return await strategy.ExecuteAsync(
            async Task<Results<Ok<RefundResponse>, ProblemHttpResult>> (CancellationToken token) =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(token);

            // FOR UPDATE without SKIP LOCKED, so two refunds of one order serialize rather than one
            // of them vanishing. The lock is on a single order row: it blocks a concurrent refund or
            // cancellation of this same order, which is exactly the race that must be serialized,
            // and nothing else in the system.
            //
            // The tenancy predicate is spelled out here because the filter is suppressed - see the
            // comment above. Soft-deleted rows are excluded for the same reason.
            var claimed = await db.Orders
                .FromSql(
                    $"""
                     SELECT *
                     FROM orders
                     WHERE order_number = {orderNumber}
                       AND demo_session_id = {sessionId}
                       AND deleted_at IS NULL
                     FOR UPDATE
                     """)
                .IgnoreQueryFilters()
                .ToListAsync(token);

            if (claimed.Count == 0)
            {
                await transaction.RollbackAsync(token);
                return OrderNotFoundProblem();
            }

            var order = claimed[0];

            // Loaded explicitly rather than by Include, which cannot be composed onto the
            // non-composable statement above without EF burying the lock in a subquery.
            await db.Entry(order).Collection(entity => entity.Lines).LoadAsync(token);
            await db.Entry(order).Collection(entity => entity.Refunds).LoadAsync(token);

            // THE KEY IS CHECKED UNDER THE LOCK, WHICH IS WHAT MAKES CHECKING IT SAFE AT ALL.
            //
            // Every refund of this order queues behind the row lock above, so a key that is absent
            // here cannot be inserted by somebody else before this transaction commits. The unique
            // index remains as the backstop for anything that reaches the table without taking the
            // lock; it is not what this path relies on, because losing on an index after the
            // gateway has already moved money would mean a refund made and not recorded.
            var replay = order.Refunds.FirstOrDefault(
                entity => string.Equals(entity.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));

            if (replay is not null)
            {
                await transaction.RollbackAsync(token);

                logger.LogInformation(
                    "Refund key {IdempotencyKey} was already spent on order {OrderNumber}. "
                    + "Replaying {Amount} rather than refunding again.",
                    idempotencyKey,
                    order.OrderNumber,
                    replay.Amount);

                return TypedResults.Ok(Describe(order, replay.RestockedUnits, replayed: true));
            }

            // Cancelling something already cancelled is a replay of a completed operation, not a
            // conflict. Answering 409 here would make a retried cancellation - the most likely
            // retry there is, since a shopper who cancels twice has usually just double-clicked -
            // look like a failure.
            if (cancelling && order.Status is OrderStatus.Cancelled)
            {
                await transaction.RollbackAsync(token);
                return TypedResults.Ok(Describe(order, restockedUnits: 0, replayed: true));
            }

            if (cancelling && !OrderStateMachine.IsLegal(order.Status, OrderStatus.Cancelled))
            {
                await transaction.RollbackAsync(token);
                return CannotCancelProblem(order.Status);
            }

            var amount = requestedAmount is { } minorUnits
                ? new Money(minorUnits, order.Currency)
                : order.RefundableRemaining;

            // A cancellation of an order that never captured anything is the ordinary case: an
            // unpaid order has no money to give back, so there is no gateway call and no ledger row.
            // Only the stock moves.
            var moneyToReturn = cancelling ? order.RefundableRemaining : amount;

            if (!cancelling)
            {
                if (order.Status is not (OrderStatus.Paid or OrderStatus.Packed or OrderStatus.Shipped))
                {
                    await transaction.RollbackAsync(token);
                    return CannotRefundProblem(order.Status);
                }

                if (moneyToReturn.IsZero || moneyToReturn > order.RefundableRemaining)
                {
                    await transaction.RollbackAsync(token);
                    return AmountProblem(moneyToReturn, order.RefundableRemaining);
                }
            }

            var restockedUnits = cancelling
                ? await ReturnReservedUnitsAsync(db, order, logger, token)
                : 0;

            Refund? refund = null;

            if (!moneyToReturn.IsZero)
            {
                // The order must be able to name the payment it is reversing. An order that is Paid
                // with no reference is a settlement written before this column existed, and it is
                // better to refuse loudly than to guess a reference and refund a stranger's payment.
                if (string.IsNullOrWhiteSpace(order.PaymentReference))
                {
                    await transaction.RollbackAsync(token);

                    logger.LogError(
                        "Order {OrderNumber} is {Status} with {Captured} captured but carries no "
                        + "payment reference, so there is nothing to refund against.",
                        order.OrderNumber,
                        order.Status,
                        order.Captured);

                    return TypedResults.Problem(
                        title: "That order cannot be refunded automatically",
                        detail: "The order captured money but does not record which payment took "
                                + "it, so there is no payment to reverse. This needs a human with "
                                + "access to the gateway's own records.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                PaymentRefundResult result;

                try
                {
                    // ASKED WHILE THE ROW LOCK IS HELD, AND THAT IS A DELIBERATE TRADE.
                    //
                    // Holding a lock across a network call is normally the wrong shape, and with a
                    // real acquirer at the other end this would become the two-phase arrangement the
                    // settlement path already uses: record the intent, commit, call, reconcile. It
                    // is not that here because the alternative is worse in the direction that
                    // matters. Releasing the lock first would let two requests with different keys
                    // both pass the "within the remaining balance" check and both reach the gateway,
                    // and the money would be gone twice before either row was written. One order row,
                    // locked for the length of one in-process gateway call, buys the guarantee that
                    // the gateway is asked exactly once per refund.
                    result = await gateway.RefundAsync(
                        new PaymentRefundRequest(
                            moneyToReturn,
                            order.PaymentReference,
                            order.OrderNumber,
                            idempotencyKey,
                            timeProvider.GetUtcNow(),
                            scenarioHint),
                        token);
                }
                catch (Exception exception)
                {
                    // The gateway could not be ASKED, which is not the same as being told no. The
                    // transaction rolls back, so the units this cancellation had just put back on
                    // the shelf go with it: nothing partial survives a gateway that is unreachable.
                    await transaction.RollbackAsync(token);

                    logger.LogError(
                        exception,
                        "The payment gateway could not be reached to refund {Amount} on order {OrderNumber}.",
                        moneyToReturn,
                        order.OrderNumber);

                    return TypedResults.Problem(
                        title: "The payment gateway could not be reached",
                        detail: "No money has moved and nothing has been recorded. Retry with the "
                                + "same idempotency key - that is what it is for, and it is what "
                                + "stops a retry becoming a second refund.",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                if (!result.IsRefunded)
                {
                    // Told no. Also a rollback, and for the same reason: no ledger row may claim
                    // money that did not move, and a cancellation that refunded nothing must not
                    // leave the order cancelled with its funds still captured.
                    await transaction.RollbackAsync(token);

                    logger.LogWarning(
                        "The gateway refused a refund of {Amount} on order {OrderNumber}: {Reason}",
                        moneyToReturn,
                        order.OrderNumber,
                        result.FailureReason);

                    return TypedResults.Problem(
                        title: "The gateway refused the refund",
                        detail: $"{result.FailureReason} No money has moved, nothing has been "
                                + "recorded, and the order is unchanged.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Only now. Everything above this line can fail without leaving a trace; everything
                // below it is recording a fact about money that has already moved.
                refund = cancelling
                    ? order.CancelAndRefund(idempotencyKey, result.GatewayReference, restockedUnits, timeProvider.GetUtcNow())
                    : order.IssueRefund(
                        moneyToReturn,
                        RefundReason.CustomerRequest,
                        idempotencyKey,
                        result.GatewayReference,
                        restockedUnits: 0,
                        timeProvider.GetUtcNow());
            }
            else if (cancelling)
            {
                // Nothing was ever captured, so Cancel's guard passes and no ledger row is written.
                // A refund of zero would be a row asserting that no money moved, which is true of
                // every moment in which nothing happened and is therefore worth recording nowhere.
                order.Cancel();
            }
            else
            {
                await transaction.RollbackAsync(token);
                return AmountProblem(moneyToReturn, order.RefundableRemaining);
            }

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            logger.LogInformation(
                "Order {OrderNumber} is now {Status}: returned {Amount} and restocked {Units} unit(s).",
                order.OrderNumber,
                order.Status,
                refund?.Amount.ToString() ?? "nothing",
                restockedUnits);

            return TypedResults.Ok(Describe(order, restockedUnits, replayed: false));
        },
        cancellationToken);
    }

    /// <summary>
    /// Puts a cancelled order's units back on the shelf, one guarded statement per reservation.
    /// <para>
    /// Guarded on <c>reserved &gt;= q</c> for the same reason every other release in this codebase
    /// is: a double release would drive the counter below zero and trip
    /// <c>ck_stock_items_reserved_non_negative</c>, turning a recoverable duplicate into a failed
    /// transaction. Zero rows affected means the ledger no longer holds what this order thinks it
    /// does — the reaper having got there first is the likely story — which is a note rather than a
    /// failure, because the cancellation is still correct.
    /// </para>
    /// <para>
    /// Only <c>reserved</c> moves, never <c>on_hand</c>. Every order a cancellation can reach is
    /// Pending or Paid, and in both the goods are still in the warehouse: <c>on_hand</c> falls when
    /// a parcel ships, and the two statuses whose parcels have shipped have no edge to Cancelled.
    /// </para>
    /// </summary>
    private static async Task<int> ReturnReservedUnitsAsync(
        VelaCommerceDbContext db,
        Order order,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Filters suppressed for the same reason as the claim: this is one transaction with the
        // locked order, and the reservation rows carry no session of their own to filter by.
        var reservations = await db.StockReservations
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OrderId == order.Id
                && entity.DeletedAt == null
                && entity.Status != ReservationStatus.Released)
            .OrderBy(entity => entity.VariantId)
            .ToListAsync(cancellationToken);

        var returned = 0;

        foreach (var reservation in reservations)
        {
            var released = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_items
                 SET reserved = reserved - {reservation.Quantity}
                 WHERE variant_id = {reservation.VariantId}
                   AND deleted_at IS NULL
                   AND reserved >= {reservation.Quantity}
                 """,
                cancellationToken);

            if (released != 1)
            {
                logger.LogWarning(
                    "Cancelling order {OrderNumber} released {Quantity} of variant {VariantId}, but "
                    + "the ledger did not hold them. The cancellation stands regardless.",
                    order.OrderNumber,
                    reservation.Quantity,
                    reservation.VariantId);

                reservation.ReturnOnCancellation();
                continue;
            }

            reservation.ReturnOnCancellation();
            returned += reservation.Quantity;
        }

        return returned;
    }

    /// <summary>
    /// Reads the key from the body or the conventional header, preferring the body when both are
    /// present and disagreeing loudly when they conflict — a client sending two different keys for
    /// one refund has a bug, and picking one silently would hide it behind a duplicate refund.
    /// </summary>
    private static bool TryReadIdempotencyKey(
        string? fromBody,
        HttpContext http,
        out string idempotencyKey,
        out ProblemHttpResult problem)
    {
        idempotencyKey = string.Empty;
        problem = null!;

        var fromHeader = http.Request.Headers[IdempotencyKeyHeader].ToString();

        var bodyKey = string.IsNullOrWhiteSpace(fromBody) ? null : fromBody.Trim();
        var headerKey = string.IsNullOrWhiteSpace(fromHeader) ? null : fromHeader.Trim();

        if (bodyKey is not null && headerKey is not null && !string.Equals(bodyKey, headerKey, StringComparison.Ordinal))
        {
            problem = TypedResults.Problem(
                title: "Missing or ambiguous idempotency key",
                detail: $"The body says '{bodyKey}' and the {IdempotencyKeyHeader} header says "
                        + $"'{headerKey}'. Send one or the same one twice: two keys for one refund "
                        + "means a retry cannot be recognised, and an unrecognised retry is a "
                        + "second refund.",
                statusCode: StatusCodes.Status400BadRequest);

            return false;
        }

        var key = bodyKey ?? headerKey;

        if (key is null)
        {
            problem = TypedResults.Problem(
                title: "Missing or ambiguous idempotency key",
                detail: $"Send an idempotency key, in the body or in the {IdempotencyKeyHeader} "
                        + "header. Without one a retried request is indistinguishable from a "
                        + "second refund, and this endpoint moves money.",
                statusCode: StatusCodes.Status400BadRequest);

            return false;
        }

        if (key.Length > 128)
        {
            problem = TypedResults.Problem(
                title: "Missing or ambiguous idempotency key",
                detail: "An idempotency key may be at most 128 characters, which is what the "
                        + "unique index that enforces it stores.",
                statusCode: StatusCodes.Status400BadRequest);

            return false;
        }

        idempotencyKey = key;
        return true;
    }

    private static RefundResponse Describe(Order order, int restockedUnits, bool replayed) =>
        new(
            order.OrderNumber,
            order.Status.ToString(),
            Amount(order.Captured),
            Amount(order.Refunded),
            Amount(order.RefundableRemaining),
            order.IsFullyRefunded,
            restockedUnits,
            replayed,
            [.. order.Refunds
                .OrderBy(refund => refund.RefundedAt)
                .ThenBy(refund => refund.Id)
                .Select(refund => new RefundLedgerEntry(
                    Amount(refund.Amount),
                    refund.Reason.ToString(),
                    refund.GatewayReference,
                    refund.RestockedUnits,
                    refund.RefundedAt))]);

    private static MoneyDto Amount(Money money) => new(money.Amount, money.Currency);

    /// <summary>
    /// Every way of failing to reach an order answers identically, matching
    /// <c>GET /api/orders/{orderNumber}</c>: a malformed number, somebody else's order and an order
    /// that does not exist are one response, so this cannot be used to discover which orders exist.
    /// </summary>
    private static ProblemHttpResult OrderNotFoundProblem() =>
        TypedResults.Problem(
            title: "No such order",
            detail: "No order with that number belongs to this visitor. Note that the signed "
                    + "retrieval token opens the order for reading but not for refunding: moving "
                    + "money requires the session that placed the order.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult CannotRefundProblem(OrderStatus status) =>
        TypedResults.Problem(
            title: "That order cannot be refunded",
            detail: status is OrderStatus.Pending
                ? "The order is still Pending, so no money has been captured yet and there is "
                  + "nothing to give back. Cancel it instead with POST "
                  + "/api/orders/{orderNumber}/cancellation, which releases the stock it is holding."
                : $"The order is {status}. A cancelled order has already had its money settled one "
                  + "way or the other, and refunding it again would return money twice.",
            statusCode: StatusCodes.Status409Conflict);

    private static ProblemHttpResult CannotCancelProblem(OrderStatus status) =>
        TypedResults.Problem(
            title: "That order cannot be cancelled",
            detail: $"The order is {status} and the parcel has already been picked or handed to the "
                    + "carrier, so cancelling it would claim the goods are still here. Refund it "
                    + "with POST /api/orders/{orderNumber}/refunds instead - the money goes back "
                    + "and the fulfilment status keeps telling the truth about where the goods are.",
            statusCode: StatusCodes.Status409Conflict);

    private static ProblemHttpResult AmountProblem(Money requested, Money remaining) =>
        TypedResults.Problem(
            title: "That refund amount cannot be accepted",
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"Asked to refund {requested}, but only {remaining} is left on this order. Nothing "
                + $"has been refunded. Refusing rather than clamping is deliberate: quietly "
                + $"returning less than was asked for is how a shopper ends up believing they were "
                + $"made whole when they were not."),
            statusCode: StatusCodes.Status409Conflict);
}
