// THE TWO INVARIANTS THIS FILE EXISTS FOR, AND THE SHAPE THAT ENFORCES THEM.
//
// 1. STOCK IS RESERVED BY A CONDITIONAL UPDATE, NEVER BY READ-THEN-WRITE.
//    StockItem.TryReserve states the rule in the domain, and it is right — but it is an in-memory
//    check on an in-memory copy, so two shoppers holding two valid StockItem instances for the last
//    unit both pass it and both save. This file therefore never loads a StockItem at all. It issues
//        UPDATE stock_items SET reserved = reserved + q WHERE variant_id = v AND on_hand - reserved >= q
//    and reads the ROW COUNT: 1 means this shopper won, 0 means they lost. The database evaluates
//    the guard and the increment in one statement against one locked row, which is the only place
//    that comparison can be made truthfully. DatabaseInvariantTests proves exactly this statement
//    against real PostgreSQL, and the ck_stock_items_reserved_within_on_hand constraint is the
//    backstop if anybody ever writes the racy version anyway.
//
// 2. A DOUBLE-SUBMITTED CHECKOUT CREATES ONE ORDER.
//    Not by SELECTing for an existing key first — two simultaneous submits both find nothing and
//    both insert, which is the race, not the fix. Both are allowed to insert, and
//    ux_orders_demo_session_id_idempotency_key picks the winner. The loser catches the unique
//    violation, rolls back (releasing its own stock reservations with it) and returns the winner's
//    order with a 200.
//
// THREE STEPS, TWO TRANSACTIONS, AND THE GATEWAY CALL BETWEEN THEM.
//
//    tx1: reserve stock, insert the order (Pending) and its reservations (Held). Commit.
//    ---: authorize the payment. No transaction is open. No row is locked.
//    tx2: apply the answer — mark paid and confirm, or cancel and release.
//
//    The tempting alternative is one transaction wrapped around all three, and it is wrong for a
//    reason that only shows up in production: an HTTP call to a payment processor inside a database
//    transaction holds row locks for the length of somebody else's network, and if the COMMIT then
//    fails the money has moved and the order does not exist. Splitting them means the failure mode
//    is instead "an order sits Pending with stock held" — visible, recoverable, and exactly what the
//    reservation expiry and the settlement webhook already exist to resolve.
//
//    Within tx1 rollback IS the compensating release: every reservation this checkout took is an
//    uncommitted increment, so abandoning the transaction returns all of them at once and no
//    partially-reserved checkout can strand stock. Only tx2's cancel path needs an explicit
//    decrementing UPDATE, because by then tx1 has committed.
//
// EVERYTHING HERE READS THROUGH THE DemoTenancy QUERY FILTER, so no query below says "where this is
// mine". The one place that is not enough is CONSTRUCTING an order: writes are not filtered, and the
// session id has to come from ICurrentDemoSession. It does, and the cart's own owner is checked
// against it before Order.FromCart is allowed to copy it.

using System.Globalization;
using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using VelaCommerce.Api.Contracts;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Domain.Payments;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Registration for the checkout surface: place an order from the current session's cart, and read
/// an order back afterwards with or without that session.
/// </summary>
public static class CheckoutEndpoints
{
    /// <summary>
    /// The conventional header for an idempotency key (IETF <c>draft-ietf-httpapi-idempotency-key-header</c>).
    /// Accepted alongside the body field so a client can follow the convention without this API
    /// inventing a second one.
    /// </summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// Log category. <c>ILogger&lt;T&gt;</c> is unavailable here because a static class cannot be a
    /// type argument, and inventing a marker type purely to satisfy the generic would be worse than
    /// naming the category once.
    /// </summary>
    private const string LogCategory = "VelaCommerce.Api.Endpoints.Checkout";

    /// <summary>
    /// Maps the checkout group. Called by the host, so this file never learns how the application is
    /// composed.
    /// </summary>
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        var checkout = app
            .MapGroup("/api")
            .WithTags("Checkout")
            .AddEndpointFilter(PreventSharedCachingAsync);

        checkout.MapPost("/checkout", PlaceOrderAsync)
            .WithName("PlaceOrder")
            .WithSummary("Place an order from the current visitor's cart")
            .WithDescription(
                "Takes the shipping address and an idempotency key; everything else - the lines, "
                + "the prices, the totals - comes from the server. Prices are revalidated against "
                + "the live catalog first, and a line that has moved fails the checkout with 409 "
                + "rather than being silently repriced in either direction. Stock is then reserved "
                + "with a conditional UPDATE per line, so losing the race for the last unit is a "
                + "409 naming the variant and not an oversell. "
                + "201 means the order was created and paid; 202 means it was created and the "
                + "gateway will settle asynchronously; 200 means this exact idempotency key had "
                + "already created an order and you are being handed that same order back, with no "
                + "second charge and no second order number. 402 means the gateway refused - the "
                + "cart survives so the shopper can try again. The response carries a signed link "
                + "that reopens the order later without a session.")
            .Produces<CheckoutOrderResponse>(StatusCodes.Status201Created)
            .Produces<CheckoutOrderResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        checkout.MapGet("/orders/{orderNumber}", GetOrderAsync)
            .WithName("GetOrder")
            .WithSummary("Read an order by its number")
            .WithDescription(
                "Two ways in. With no token the order must belong to the calling session, which is "
                + "the ordinary 'my orders' path. With the signed token from the checkout response "
                + "the order opens for anyone holding the link, whatever session they are in and "
                + "whether or not they have one - that is what makes a confirmation link "
                + "forwardable and survivable across a cleared cookie. Every failure is 404: a "
                + "malformed number, an expired or forged token, and an order belonging to someone "
                + "else all answer identically, so the endpoint cannot be used to find out which "
                + "order numbers exist.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Marks every response here uncacheable by anything that is not this browser.
    /// <para>
    /// An order carries a name, an address and a capability token. A shared cache in front of the
    /// demo would see a GET with no <c>Authorization</c> header and treat it as public — and the
    /// retrieval token sits in the query string, which is exactly the part of a URL caches key on.
    /// Applied to the group so an endpoint added here later cannot forget it.
    /// </para>
    /// </summary>
    private static async ValueTask<object?> PreventSharedCachingAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Vary = "Cookie";

        return await next(context);
    }

    private static async Task<Results<
        Ok<CheckoutOrderResponse>,
        Created<CheckoutOrderResponse>,
        Accepted<CheckoutOrderResponse>,
        ProblemHttpResult>> PlaceOrderAsync(
        CheckoutRequest request,
        HttpContext http,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        IPaymentGateway gateway,
        IDataProtectionProvider dataProtection,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Writes have no safe degradation without a session: the row needs an owner, and the only
        // acceptable source for one is the bound session. Unreachable in the composed host.
        if (session.SessionId is not { } sessionId)
        {
            return NoDemoSessionProblem();
        }

        var (idempotencyKey, keyProblem) = ResolveIdempotencyKey(http.Request, request);
        if (keyProblem is not null)
        {
            return keyProblem;
        }

        ShippingAddress address;
        try
        {
            address = ToDomainAddress(request.ShippingAddress);
            address.Validate();
        }
        catch (DomainException exception)
        {
            return AddressProblem(exception);
        }

        // The clock is read once, here, and the same instant stamps the order, every reservation's
        // expiry, the payment request and — if it settles synchronously — the capture. Reading it
        // again further down would let one checkout's rows disagree with each other by however long
        // the gateway took, and the architecture test forbids reading it at all outside a seam.
        var now = clock.GetUtcNow();

        // EnableRetryOnFailure is configured on the context, and a retrying execution strategy
        // refuses user-initiated transactions unless the whole transaction is handed to it — it has
        // to be able to run the entire unit again, not resume half of one. Every attempt therefore
        // starts from a cleared change tracker and re-reads the cart, so a retry cannot re-insert
        // the order the previous attempt already added.
        var placement = await db.Database
            .CreateExecutionStrategy()
            .ExecuteAsync(
                (CancellationToken token) =>
                    ReserveAndPlaceAsync(db, sessionId, idempotencyKey!, address, now, token),
                cancellationToken);

        switch (placement.Status)
        {
            case PlacementStatus.EmptyCart:
                return EmptyCartProblem();

            case PlacementStatus.PriceMoved:
                return PriceMovedProblem(placement.PriceChanges!);

            case PlacementStatus.OutOfStock:
                return OutOfStockProblem(placement.Shortfall!);

            case PlacementStatus.Refused:
                return DomainProblem(placement.Detail!);

            case PlacementStatus.SessionMismatch:
                logger.LogError(
                    "Refused to place an order: the cart read for session {SessionId} reported a "
                    + "different owner. The DemoTenancy query filter should make this impossible.",
                    sessionId);
                return TenancyMismatchProblem();

            case PlacementStatus.NumberCollision:
                logger.LogError(
                    "Two orders were minted with the same order number. {Sequence} is generating "
                    + "values it has already generated - it has most likely been reset or restored "
                    + "out of step with the orders table.",
                    OrderNumbers.SequenceName);
                return OrderNumberCollisionProblem();

            case PlacementStatus.ReplayLost:
                logger.LogError(
                    "A checkout lost the idempotency race for key {IdempotencyKey} but the winning "
                    + "order could not then be read back.",
                    idempotencyKey);
                return ReplayLostProblem();
        }

        if (placement.Order is not { } order)
        {
            return ReplayLostProblem();
        }

        // A REPLAY IS ANSWERED WITHOUT TOUCHING THE GATEWAY. The first submit already authorized;
        // asking again would be a second authorization for one shopper's one intention, which is
        // the charge the idempotency key exists to prevent.
        if (placement.Status is PlacementStatus.Replayed)
        {
            // Answer for the order that actually exists, not for the one the caller hoped for.
            // A flat 200 here told four different non-successes they had succeeded — a declined
            // card, an abandoned checkout, a settlement still in flight, and the retry the
            // gateway-unreachable problem document explicitly recommends. The second submit of a
            // double-click is exactly the request a naive client renders as a confirmation page,
            // so the status code has to carry the truth even though the body always did.
            var replayed = Describe(order, dataProtection, payment: null);

            return order.Status switch
            {
                OrderStatus.Cancelled => ReplayedButNotPaidProblem(order.OrderNumber, order.Status),
                OrderStatus.Pending => TypedResults.Accepted(replayed.RetrievalPath, replayed),
                _ => TypedResults.Ok(replayed),
            };
        }

        PaymentAuthorizationResult authorization;
        try
        {
            authorization = await gateway.AuthorizeAsync(
                new PaymentAuthorizationRequest(
                    order.Total,
                    order.OrderNumber,
                    idempotencyKey!,
                    now,
                    request.PaymentScenario),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The port's contract: an exception means the gateway could not be ASKED, which is not
            // the same as being told no. The order therefore stays Pending with its stock held
            // rather than being cancelled, because cancelling would be a guess about money that may
            // already have moved. The reservation expiry is what stops that guess being permanent.
            logger.LogError(
                exception,
                "The payment gateway could not be reached for order {OrderNumber}. It stays Pending.",
                order.OrderNumber);

            return GatewayUnreachableProblem(order.OrderNumber);
        }

        var payment = new CheckoutPaymentResponse(
            authorization.Outcome.ToString(),
            authorization.GatewayReference,
            authorization.DeclineReason?.ToString(),
            authorization.IsCaptured,
            authorization.AwaitsSettlement);

        // ABANDONED CHANGES NOTHING. Nobody refused, so there is nothing to cancel and nothing to
        // release: the order is left Pending and its reservation is left to lapse on its own TTL,
        // which is the behaviour the simulator's scenario table documents and the case the reaper
        // exists to demonstrate. Skipping the second transaction entirely is also the honest
        // encoding of "no state change".
        if (authorization.Outcome is PaymentOutcome.Abandoned)
        {
            return PaymentNotCompletedProblem(order.OrderNumber, payment);
        }

        Order? settled;
        try
        {
            // CancellationToken.None, deliberately. Past this line the gateway has already given an
            // answer, and for a capture that means the money has moved. Abandoning the write
            // because the shopper closed the tab would leave a paid order sitting in Pending with
            // nothing to reconcile it against — the one outcome worse than a slow response.
            settled = await SettleAsync(db, order.Id, authorization, now, logger, CancellationToken.None);
        }
        catch (Exception exception) when (exception is DomainException or DbUpdateException)
        {
            // DbUpdateException is caught alongside DomainException because the settle
            // transaction also clears the cart, and two checkouts in flight for one visitor
            // delete the same cart lines — the loser gets DbUpdateConcurrencyException. Left
            // uncaught it rolled back the whole transaction including MarkPaid, so a payment
            // the gateway had already captured vanished and the order stayed Pending with
            // nothing to reconcile against. That is the exact outcome this file's two-phase
            // split exists to prevent, so it must not escape as a bare 500.
            logger.LogCritical(
                exception,
                "Order {OrderNumber} could not be settled after the gateway answered "
                + "{Outcome} for {GatewayReference}. This needs reconciling by hand.",
                order.OrderNumber,
                authorization.Outcome,
                authorization.GatewayReference);

            return SettlementFailedProblem(order.OrderNumber);
        }

        if (settled is null)
        {
            logger.LogError(
                "Order {OrderNumber} vanished between being committed and being settled.",
                order.OrderNumber);

            return SettlementFailedProblem(order.OrderNumber);
        }

        var response = Describe(settled, dataProtection, payment);

        if (authorization.Outcome is PaymentOutcome.Declined)
        {
            // 402 rather than a 200 carrying an outcome field. A client that renders success on any
            // 2xx would otherwise show a confirmation page for an order nobody paid for, and that
            // client will exist.
            return PaymentNotCompletedProblem(settled.OrderNumber, payment);
        }

        return authorization.AwaitsSettlement
            ? TypedResults.Accepted(response.RetrievalPath, response)
            : TypedResults.Created(response.RetrievalPath, response);
    }

    /// <summary>
    /// One attempt at the first transaction: reserve every line, then insert the order.
    /// <para>
    /// Returns a <see cref="Placement"/> rather than throwing for the outcomes that are business
    /// answers — an empty cart, a price that moved, a lost race for the last unit, a replayed key.
    /// Only genuine faults leave by exception, which is what lets the retrying execution strategy
    /// above distinguish "run this again" from "tell the shopper".
    /// </para>
    /// </summary>
    private static async Task<Placement> ReserveAndPlaceAsync(
        VelaCommerceDbContext db,
        Guid sessionId,
        string idempotencyKey,
        ShippingAddress address,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        // A FAST PATH, NOT THE MECHANISM — and the distinction matters, because "SELECT then INSERT"
        // is precisely the broken idempotency this checkout is supposed to improve on. Two
        // simultaneous first submits both read nothing here and both go on to insert; the unique
        // index below is what makes one of them lose, and it stays the only guarantee.
        //
        // This read exists for the ordinary replay, which arrives seconds or minutes later, and it
        // is not merely an optimisation. Placing an order EMPTIES THE CART, so by the time a
        // double-click or a retry arrives there is nothing left to check out — without this the
        // replay would be refused as an empty cart and never reach the index that knows better.
        // It also keeps a replay from taking stock it will immediately roll back.
        var replayed = await LoadOrderByIdempotencyKeyAsync(db, idempotencyKey, cancellationToken);
        if (replayed is not null)
        {
            return new Placement(PlacementStatus.Replayed, Order: replayed);
        }

        var cart = await db.Carts
            .Include(entity => entity.Lines)
            .OrderByDescending(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (cart is null || cart.IsEmpty)
        {
            return new Placement(PlacementStatus.EmptyCart);
        }

        // Defence in depth, not a filter the query forgot. Order.FromCart copies the cart's owner
        // onto the order, so if a cart from another visitor ever reached this method the order would
        // be written under their id — a write, and therefore not covered by the read-side tenancy
        // filter at all. Cheap to check, and it fails loudly rather than quietly mis-attributing.
        if (cart.DemoSessionId != sessionId)
        {
            return new Placement(PlacementStatus.SessionMismatch);
        }

        var priceChanges = await FindPriceChangesAsync(db, cart, cancellationToken);
        if (priceChanges.Count > 0)
        {
            return new Placement(PlacementStatus.PriceMoved, PriceChanges: priceChanges);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Ordered by variant id so that every checkout takes its row locks in the same sequence.
        // Two shoppers buying the same two SKUs in opposite cart order would otherwise be a textbook
        // deadlock: each holds the row the other needs next, and PostgreSQL resolves it by killing
        // one of them with an error the shopper did nothing to deserve.
        foreach (var line in cart.Lines.OrderBy(entity => entity.VariantId))
        {
            var reserved = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_items
                 SET reserved = reserved + {line.Quantity}
                 WHERE variant_id = {line.VariantId}
                   AND deleted_at IS NULL
                   AND on_hand - reserved >= {line.Quantity}
                 """,
                cancellationToken);

            if (reserved == 1)
            {
                continue;
            }

            // Zero rows means the guard failed: not enough free units, or no stock ledger for this
            // variant at all. Either way the shopper lost, and losing is a 409, not an exception.
            // Rolling back first releases every line already taken in this attempt, so the
            // availability figure read next is the real one and no stock is stranded.
            await transaction.RollbackAsync(cancellationToken);

            var available = await db.StockItems
                .AsNoTracking()
                .Where(stock => stock.VariantId == line.VariantId)
                .Select(stock => (int?)(stock.OnHand - stock.Reserved))
                .FirstOrDefaultAsync(cancellationToken);

            return new Placement(
                PlacementStatus.OutOfStock,
                Shortfall: new CheckoutStockShortfall(
                    line.VariantId,
                    line.Sku,
                    line.DisplayName,
                    line.Quantity,
                    available));
        }

        var quote = CheckoutPricing.Quote(cart.Subtotal);

        Order order;
        try
        {
            order = Order.FromCart(
                cart,
                OrderNumbers.Format(await NextOrderSequenceValueAsync(db, cancellationToken)),
                idempotencyKey,
                address,
                quote.Shipping,
                quote.Tax,
                now);
        }
        catch (DomainException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new Placement(PlacementStatus.Refused, Detail: exception.Message);
        }

        db.Orders.Add(order);

        // One reservation row per line, all expiring together. They are the audit trail for the
        // stock_items increments above: the increments say how many units are promised, these say
        // to whom and until when, and the reaper needs the second to undo the first.
        foreach (var line in cart.Lines)
        {
            db.StockReservations.Add(
                new StockReservation(
                    line.VariantId,
                    order.Id,
                    line.Quantity,
                    now + CheckoutPolicy.ReservationWindow));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (CheckoutConflicts.IsIdempotencyReplay(exception))
        {
            // The shopper double-submitted, or a retry arrived. The other request's order is the
            // real one; this attempt rolls back, which also returns the stock it just reserved.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();

            var existing = await LoadOrderByIdempotencyKeyAsync(db, idempotencyKey, cancellationToken);

            return existing is null
                ? new Placement(PlacementStatus.ReplayLost)
                : new Placement(PlacementStatus.Replayed, Order: existing);
        }
        catch (DbUpdateException exception) when (CheckoutConflicts.IsOrderNumberCollision(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new Placement(PlacementStatus.NumberCollision);
        }

        await transaction.CommitAsync(cancellationToken);

        return new Placement(PlacementStatus.Placed, Order: order);
    }

    /// <summary>
    /// The second transaction: applies the gateway's answer to the committed order.
    /// <para>
    /// Reloads everything from scratch, because a retrying execution strategy may run this body more
    /// than once and the aggregate refuses to repeat itself — <c>Paid -> Paid</c> is not a legal
    /// edge, deliberately, so replaying a settlement against an already-settled order throws rather
    /// than passing silently. Reloading turns that into the harmless "already settled, nothing to
    /// do" branch below.
    /// </para>
    /// </summary>
    private static async Task<Order?> SettleAsync(
        VelaCommerceDbContext db,
        Guid orderId,
        PaymentAuthorizationResult authorization,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async (CancellationToken token) =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var order = await db.Orders
                    .Include(entity => entity.Lines)
                    .FirstOrDefaultAsync(entity => entity.Id == orderId, token);

                if (order is null)
                {
                    await transaction.RollbackAsync(token);
                    return null;
                }

                if (order.Status is not OrderStatus.Pending)
                {
                    // Somebody else settled it first. Nothing to apply, and nothing to complain
                    // about: the state machine already refused the duplicate on our behalf.
                    await transaction.RollbackAsync(token);
                    return order;
                }

                var held = await db.StockReservations
                    .Where(entity => entity.OrderId == orderId && entity.Status == ReservationStatus.Held)
                    .ToListAsync(token);

                if (authorization.IsCaptured)
                {
                    // MarkPaid refuses a capture that does not equal the total to the cent, which is
                    // why the amount comes from the gateway's answer rather than from the order.
                    order.MarkPaid(authorization.Amount, now);

                    foreach (var reservation in held)
                    {
                        reservation.Confirm();
                    }

                    // The units stay reserved on the ledger. They are promised, not gone: on_hand
                    // only drops when the parcel ships, which is StockItem.Ship's job and not this
                    // request's.
                    await TryClearCartAsync(db, logger, token);
                }
                else if (authorization.AwaitsSettlement)
                {
                    // Pending, held, and waiting for a signed webhook. The cart is still emptied:
                    // the order has captured its own line snapshot and is the shopper's record of
                    // the purchase, and leaving the cart full would invite a second checkout of the
                    // same goods while the first is still settling.
                    await TryClearCartAsync(db, logger, token);
                }
                else
                {
                    // Declined. The order is CANCELLED and kept rather than rolled away, for three
                    // reasons. The state machine has a Pending -> Cancelled edge for exactly this.
                    // The attempt really happened — stock was held and released — and an order that
                    // never existed cannot say so. And most practically, the row is what keeps the
                    // idempotency key spent: without it a frantically re-clicked "Pay" would mint a
                    // new order number each time, and a new order number means a new gateway
                    // reference, which means the gateway's own idempotency can no longer collapse
                    // the attempts into one charge. The cart is left intact so the shopper can fix
                    // their card and try again with a fresh key.
                    order.Cancel();

                    foreach (var reservation in held)
                    {
                        reservation.Release();
                    }

                    foreach (var line in order.Lines.OrderBy(entity => entity.VariantId))
                    {
                        var released = await db.Database.ExecuteSqlAsync(
                            $"""
                             UPDATE stock_items
                             SET reserved = reserved - {line.Quantity}
                             WHERE variant_id = {line.VariantId}
                               AND deleted_at IS NULL
                               AND reserved >= {line.Quantity}
                             """,
                            token);

                        if (released != 1)
                        {
                            // Guarded so a double release can never drive reserved below zero and
                            // trip the check constraint. Zero rows means the ledger no longer holds
                            // what this order thinks it does — the reaper got there first, most
                            // likely — so the cancellation still stands and this is a note, not a
                            // failure.
                            logger.LogWarning(
                                "Releasing {Quantity} of variant {VariantId} for cancelled order "
                                + "{OrderNumber} affected no rows; the stock ledger no longer holds "
                                + "that reservation.",
                                line.Quantity,
                                line.VariantId,
                                order.OrderNumber);
                        }
                    }
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return order;
            },
            cancellationToken);
    }

    private static async Task<Results<Ok<CheckoutOrderResponse>, ProblemHttpResult>> GetOrderAsync(
        string orderNumber,
        string? token,
        VelaCommerceDbContext db,
        IDataProtectionProvider dataProtection,
        CancellationToken cancellationToken)
    {
        // Rejected before the database is touched, and rejected as 404 rather than 400. Every way of
        // failing to reach an order answers identically, so nobody can use the difference between
        // two error codes to learn which references are real.
        if (!OrderNumbers.TryNormalize(orderNumber, out var normalized))
        {
            return OrderNotFoundProblem();
        }

        Order? order = null;

        if (OrderRetrievalToken.TryRead(dataProtection, token, out var tokenOrderId))
        {
            // THE TOKEN IS A CAPABILITY, WHICH IS NOT THE SAME THING AS TENANCY, AND THE DIFFERENCE
            // IS WHY IT MAY STAND OUTSIDE THE FILTER.
            //
            // The session cookie is an AMBIENT IDENTITY: it arrives on every request, it says who
            // the caller is, and it grants access to everything that visitor owns, forever. The
            // DemoTenancy filter is the machinery that makes that safe by default.
            //
            // This token is a BEARER CAPABILITY: it names one order, it expires, it is handed out
            // deliberately, and it says nothing about who is holding it. That is the property a
            // confirmation link needs — the shopper must be able to open their receipt tomorrow from
            // another device, after clearing cookies, or forward it to whoever is paying. Neither
            // mechanism can do the other's job: widening the cookie to cover other people's orders
            // would be a security hole, and minting a session cookie for a stranger who followed a
            // link would silently adopt them into somebody else's cart.
            //
            // So exactly one filter is suppressed, by name, and SoftDelete stays in force. The token
            // proves the caller was given this order's link; it is not permission to see anything
            // else, which is why the order number in the route must still match the id inside the
            // token.
            order = await db.Orders
                .AsNoTracking()
                .Include(entity => entity.Lines)
                .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                .FirstOrDefaultAsync(entity => entity.Id == tokenOrderId, cancellationToken);

            if (order is not null && !string.Equals(order.OrderNumber, normalized, StringComparison.Ordinal))
            {
                order = null;
            }
        }

        // No token, or one that did not open anything: fall back to "is this order mine", which the
        // tenancy filter answers without a WHERE clause here.
        order ??= await db.Orders
            .AsNoTracking()
            .Include(entity => entity.Lines)
            .FirstOrDefaultAsync(entity => entity.OrderNumber == normalized, cancellationToken);

        return order is null
            ? OrderNotFoundProblem()
            : TypedResults.Ok(Describe(order, dataProtection, payment: null));
    }

    /// <summary>
    /// Finds the order a key has already created, if there is one.
    /// <para>
    /// No <c>WHERE demo_session_id = ...</c>, and none is needed: the DemoTenancy filter supplies
    /// it, and the unique index is scoped the same way, so "this key's order" means the same set of
    /// rows to both. Two visitors reusing an obvious key like <c>1</c> therefore never see each
    /// other's order, which is what makes an unguessable key unnecessary here.
    /// </para>
    /// <para>
    /// Untracked, because it is only ever read: the caller either returns it or discards it, and
    /// tracking it would let a later <c>SaveChanges</c> in the same request write to an order this
    /// request did not create.
    /// </para>
    /// </summary>
    private static Task<Order?> LoadOrderByIdempotencyKeyAsync(
        VelaCommerceDbContext db,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        db.Orders
            .AsNoTracking()
            .Include(entity => entity.Lines)
            .FirstOrDefaultAsync(entity => entity.IdempotencyKey == idempotencyKey, cancellationToken);

    /// <summary>
    /// Compares every cart line against the live catalog and reports the ones that moved.
    /// <para>
    /// One extra query for the whole cart rather than a correlated subquery per line, matching what
    /// the cart's own read does. A variant that has left the catalog is absent from the dictionary
    /// and is reported as a change with no current price, because "there is no price any more" is a
    /// reason to stop just as much as "the price is different".
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<CheckoutPriceChange>> FindPriceChangesAsync(
        VelaCommerceDbContext db,
        Cart cart,
        CancellationToken cancellationToken)
    {
        var variantIds = cart.Lines.Select(line => line.VariantId).Distinct().ToArray();

        var live = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variantIds.Contains(variant.Id) && variant.DeletedAt == null)
            .Select(variant => new { variant.Id, Amount = variant.Price.Amount })
            .ToDictionaryAsync(row => row.Id, row => row.Amount, cancellationToken);

        var changes = new List<CheckoutPriceChange>();

        foreach (var line in cart.Lines)
        {
            var captured = new MoneyDto(line.UnitPrice.Amount, line.UnitPrice.Currency);

            if (!live.TryGetValue(line.VariantId, out var liveAmount))
            {
                changes.Add(new CheckoutPriceChange(
                    line.VariantId, line.Sku, line.DisplayName, captured, Now: null, Difference: null));
                continue;
            }

            if (liveAmount == line.UnitPrice.Amount)
            {
                continue;
            }

            changes.Add(new CheckoutPriceChange(
                line.VariantId,
                line.Sku,
                line.DisplayName,
                captured,
                new MoneyDto(liveAmount, line.UnitPrice.Currency),
                new MoneyDto(liveAmount - line.UnitPrice.Amount, line.UnitPrice.Currency)));
        }

        return changes;
    }

    /// <summary>
    /// Draws the next order-number seed from the database sequence.
    /// <para>
    /// A sequence rather than anything computed in this process, because uniqueness across
    /// concurrent requests is exactly what a sequence is for and exactly what a process-local
    /// counter or a random draw cannot promise. <c>nextval</c> is non-transactional on purpose: a
    /// rolled-back checkout burns its number and leaves a gap, which is a feature here — gaps are
    /// what stop the sequence being read as a sales figure.
    /// </para>
    /// <para>
    /// Two details in the SQL are load-bearing. The column alias is quoted because EF composes a
    /// scalar <c>SqlQuery</c> over a subquery and looks for a column literally named <c>Value</c>,
    /// while PostgreSQL folds unquoted identifiers to lower case. And the sequence name is written
    /// out rather than interpolated from <see cref="OrderNumbers.SequenceName"/>, because EF turns
    /// every hole in an interpolated SQL string into a bound parameter and a parameter cannot stand
    /// in for an identifier — the two spellings are kept in step by the constant's own comment.
    /// </para>
    /// </summary>
    private static Task<long> NextOrderSequenceValueAsync(
        VelaCommerceDbContext db,
        CancellationToken cancellationToken) =>
        db.Database
            .SqlQuery<long>($"SELECT nextval('order_number_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

    /// <summary>
    /// Empties the visitor's cart. The order holds its own line snapshot, so nothing is lost, and
    /// the cart row survives so the next add does not have to recreate it.
    /// </summary>
    private static async Task ClearCartAsync(VelaCommerceDbContext db, CancellationToken cancellationToken)
    {
        var cart = await db.Carts
            .Include(entity => entity.Lines)
            .OrderByDescending(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (cart is { IsEmpty: false })
        {
            cart.Clear();
        }
    }

    /// <summary>
    /// Clears the cart without letting that failure reach the capture.
    /// <para>
    /// Emptying the cart is housekeeping: the order already carries its own line snapshot, so a
    /// cart that survives a successful checkout is untidy, not wrong. A capture that is rolled
    /// back because two tabs raced to delete the same rows is very wrong. This swallows the
    /// concurrency loss deliberately, and the reaper that follows will not care either.
    /// </para>
    /// </summary>
    private static async Task TryClearCartAsync(
        VelaCommerceDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await ClearCartAsync(db, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogInformation(
                exception,
                "A concurrent checkout had already emptied this cart. The order is unaffected.");
        }
    }

    /// <summary>
    /// Finds the idempotency key, from the conventional header or the body, and refuses ambiguity.
    /// <para>
    /// Both are accepted because the header is the interoperable convention and the body is what a
    /// generated client from the OpenAPI document will reach for first. Two copies that disagree is
    /// the one case that must not be resolved by picking a winner: whichever this code chose, the
    /// caller believed the other, and the entire value of the key is that both sides mean the same
    /// checkout by it.
    /// </para>
    /// </summary>
    private static (string? Key, ProblemHttpResult? Problem) ResolveIdempotencyKey(
        HttpRequest httpRequest,
        CheckoutRequest request)
    {
        string? fromHeader = null;

        if (httpRequest.Headers.TryGetValue(IdempotencyKeyHeader, out var headerValues))
        {
            if (headerValues.Count > 1)
            {
                return (null, IdempotencyKeyProblem(
                    $"The request carries {headerValues.Count} {IdempotencyKeyHeader} headers. "
                    + "One checkout means one key."));
            }

            fromHeader = headerValues.ToString().Trim();
        }

        var fromBody = request.IdempotencyKey?.Trim();

        if (!string.IsNullOrEmpty(fromHeader)
            && !string.IsNullOrEmpty(fromBody)
            && !string.Equals(fromHeader, fromBody, StringComparison.Ordinal))
        {
            return (null, IdempotencyKeyProblem(
                $"The {IdempotencyKeyHeader} header and the body's idempotencyKey disagree. Send "
                + "one, or send both with the same value."));
        }

        var key = string.IsNullOrEmpty(fromHeader) ? fromBody : fromHeader;

        if (string.IsNullOrEmpty(key))
        {
            return (null, IdempotencyKeyProblem(
                $"An idempotency key is required, in the {IdempotencyKeyHeader} header or as "
                + "idempotencyKey in the body. It is what makes a double-submitted checkout create "
                + "one order instead of two, so there is no safe default for it and the server "
                + "cannot invent one on the caller's behalf."));
        }

        if (key.Length > CheckoutPolicy.MaxIdempotencyKeyLength)
        {
            return (null, IdempotencyKeyProblem(
                $"The idempotency key is {key.Length} characters; the limit is "
                + $"{CheckoutPolicy.MaxIdempotencyKeyLength}."));
        }

        if (key.Any(char.IsControl))
        {
            return (null, IdempotencyKeyProblem("The idempotency key contains control characters."));
        }

        return (key, null);
    }

    /// <summary>
    /// Builds the domain address from the request, coercing absent fields to empty strings so that
    /// <c>ShippingAddress.Validate()</c> — and not this method — decides what is missing and says so
    /// in the domain's own words.
    /// </summary>
    private static ShippingAddress ToDomainAddress(CheckoutAddressRequest? request)
    {
        if (request is null)
        {
            throw new DomainException("A shipping address is required.");
        }

        return new ShippingAddress
        {
            Recipient = request.Recipient?.Trim() ?? string.Empty,
            Line1 = request.Line1?.Trim() ?? string.Empty,
            Line2 = NullIfBlank(request.Line2),
            City = request.City?.Trim() ?? string.Empty,
            Region = NullIfBlank(request.Region),
            PostalCode = request.PostalCode?.Trim() ?? string.Empty,
            CountryCode = request.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty,
        };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CheckoutOrderResponse Describe(
        Order order,
        IDataProtectionProvider dataProtection,
        CheckoutPaymentResponse? payment)
    {
        var token = OrderRetrievalToken.Issue(dataProtection, order.Id);

        return new CheckoutOrderResponse(
            order.OrderNumber,
            order.Status.ToString(),
            order.PlacedAt,
            order.PaidAt,
            order.Currency,
            [.. order.Lines
                .OrderBy(line => line.Id)
                .Select(line => new CheckoutOrderLineResponse(
                    line.VariantId,
                    line.Sku,
                    line.DisplayName,
                    Amount(line.UnitPrice),
                    line.Quantity))],
            Amount(order.Subtotal),
            Amount(order.Shipping),
            Amount(order.Tax),
            Amount(order.Total),
            Amount(order.Captured),
            Amount(order.Refunded),
            new CheckoutAddressResponse(
                order.ShippingAddress.Recipient,
                order.ShippingAddress.Line1,
                order.ShippingAddress.Line2,
                order.ShippingAddress.City,
                order.ShippingAddress.Region,
                order.ShippingAddress.PostalCode,
                order.ShippingAddress.CountryCode),
            token,
            $"/api/orders/{order.OrderNumber}?token={Uri.EscapeDataString(token)}",
            payment);
    }

    private static MoneyDto Amount(Money money) => new(money.Amount, money.Currency);

    private static ProblemHttpResult IdempotencyKeyProblem(string detail) =>
        TypedResults.Problem(
            title: "Missing or ambiguous idempotency key",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Turns a broken invariant into a 400. The domain's own wording is passed through because it
    /// already names the rule — "Country must be an ISO alpha-2 code" is a better error than
    /// anything this handler could re-derive from the outside — and because letting a
    /// <see cref="DomainException"/> reach the exception handler would report a caller's mistake as
    /// a 500.
    /// </summary>
    private static ProblemHttpResult DomainProblem(string detail) =>
        TypedResults.Problem(
            title: "That checkout cannot be accepted",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult AddressProblem(DomainException exception) =>
        DomainProblem(exception.Message);

    private static ProblemHttpResult EmptyCartProblem() =>
        TypedResults.Problem(
            title: "The cart is empty",
            detail: "There is nothing to buy. Add an item with POST /api/cart/items first. An "
                    + "empty cart is a 400 rather than a 404 because the cart is not missing - it "
                    + "exists and holds nothing, which is a request that cannot be fulfilled rather "
                    + "than a resource that cannot be found.",
            statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult PriceMovedProblem(IReadOnlyList<CheckoutPriceChange> changes) =>
        TypedResults.Problem(
            title: "Prices have changed since these items were added",
            detail: $"{changes.Count} line(s) no longer cost what the cart says. Nothing has been "
                    + "charged and no stock has been taken. Re-read GET /api/cart, show the "
                    + "shopper the new total and check out again once they have agreed to it - the "
                    + "server will not quietly charge either the old price or the new one.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["priceChanges"] = changes });

    private static ProblemHttpResult OutOfStockProblem(CheckoutStockShortfall shortfall) =>
        TypedResults.Problem(
            title: "Not enough stock left",
            detail: $"{shortfall.DisplayName} ({shortfall.Sku}) could not be reserved: {shortfall.Requested} "
                    + $"requested, {shortfall.Available?.ToString(CultureInfo.InvariantCulture) ?? "no stock record"} available. "
                    + "Nothing has been charged, no order exists and every unit this attempt had "
                    + "already reserved has been returned. Reduce the quantity or remove the line.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["shortfall"] = shortfall });

    private static ProblemHttpResult PaymentNotCompletedProblem(
        string orderNumber,
        CheckoutPaymentResponse payment) =>
        TypedResults.Problem(
            title: payment.Outcome == nameof(PaymentOutcome.Declined)
                ? "The payment was declined"
                : "The payment was not completed",
            detail: payment.Outcome == nameof(PaymentOutcome.Declined)
                ? $"Order {orderNumber} has been cancelled and its stock released. The cart is "
                  + "untouched, so the shopper can try again - with a NEW idempotency key, since "
                  + "this one now belongs to the cancelled order."
                : $"Order {orderNumber} was created but never paid for. It stays Pending and its "
                  + "stock stays reserved until the reservation lapses, because nobody refused the "
                  + "payment - the shopper simply never finished it.",
            statusCode: StatusCodes.Status402PaymentRequired,
            extensions: new Dictionary<string, object?>
            {
                ["orderNumber"] = orderNumber,
                ["payment"] = payment,
            });

    private static ProblemHttpResult GatewayUnreachableProblem(string orderNumber) =>
        TypedResults.Problem(
            title: "The payment provider could not be reached",
            detail: $"Order {orderNumber} exists and is Pending; its stock is reserved. Nothing is "
                    + "known about whether money moved, so the order has deliberately not been "
                    + "cancelled. Read GET /api/orders/{orderNumber} in a moment, or retry the "
                    + "checkout with the SAME idempotency key - that is what makes the retry safe.",
            statusCode: StatusCodes.Status502BadGateway,
            extensions: new Dictionary<string, object?> { ["orderNumber"] = orderNumber });

    private static ProblemHttpResult SettlementFailedProblem(string orderNumber) =>
        TypedResults.Problem(
            title: "The payment was answered but the order could not be settled",
            detail: $"Order {orderNumber} needs reconciling by hand: the gateway gave an answer that "
                    + "could not be applied to the order. This has been logged at Critical.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["orderNumber"] = orderNumber });

    /// <summary>
    /// Answers a replayed checkout whose original never resulted in a paid order.
    /// <para>
    /// Separate from <see cref="PaymentNotCompletedProblem"/> because a replay has no fresh
    /// gateway answer to report — the whole point of the idempotency key is that the second
    /// submit does not authorize again. What the caller needs to know is the order's settled
    /// state and that retrying under the same key will keep returning it.
    /// </para>
    /// </summary>
    private static ProblemHttpResult ReplayedButNotPaidProblem(string orderNumber, OrderStatus status) =>
        TypedResults.Problem(
            title: "That checkout did not result in a payment",
            detail: $"Order {orderNumber} already exists for this idempotency key and is {status}. "
                    + "It was not paid, so this is not a confirmation. Start a new checkout with a "
                    + "new idempotency key; replaying this one will keep returning this same answer.",
            statusCode: StatusCodes.Status402PaymentRequired);

    private static ProblemHttpResult ReplayLostProblem() =>
        TypedResults.Problem(
            title: "Checkout could not be completed",
            detail: "This idempotency key already belongs to an order, but that order could not be "
                    + "read back. Nothing was charged and no second order was created. Retry with "
                    + "the same key.",
            statusCode: StatusCodes.Status500InternalServerError);

    private static ProblemHttpResult OrderNumberCollisionProblem() =>
        TypedResults.Problem(
            title: "Checkout could not be completed",
            detail: "The generated order number was already taken, which should be impossible. "
                    + "Nothing was charged and no order was created.",
            statusCode: StatusCodes.Status500InternalServerError);

    private static ProblemHttpResult TenancyMismatchProblem() =>
        TypedResults.Problem(
            title: "Checkout could not be completed",
            detail: "The cart read for this visitor reported a different owner, so no order was "
                    + "created. This is a server fault and has been logged.",
            statusCode: StatusCodes.Status500InternalServerError);

    private static ProblemHttpResult OrderNotFoundProblem() =>
        TypedResults.Problem(
            title: "No such order",
            detail: "There is no order with that number for this visitor, and no valid retrieval "
                    + "token was supplied for one. A number that does not exist, a token that has "
                    + "expired or been tampered with, and somebody else's order all answer the "
                    + "same way on purpose.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult NoDemoSessionProblem() =>
        TypedResults.Problem(
            title: "No demo session",
            detail: "This request has no demo session, so there is no visitor to own an order. "
                    + "Writes refuse rather than guess an owner.",
            statusCode: StatusCodes.Status500InternalServerError);

    /// <summary>How the first transaction ended. Every value except the first two is a dead end.</summary>
    private enum PlacementStatus
    {
        /// <summary>A new order was committed. It still needs paying for.</summary>
        Placed,

        /// <summary>This key had already created an order; that one is returned unchanged.</summary>
        Replayed,

        /// <summary>Nothing to buy.</summary>
        EmptyCart,

        /// <summary>At least one line's price moved, or its variant left the catalog.</summary>
        PriceMoved,

        /// <summary>A line lost the race for the units it needed.</summary>
        OutOfStock,

        /// <summary>The domain refused to build the order.</summary>
        Refused,

        /// <summary>The cart's owner was not the calling session. Should be unreachable.</summary>
        SessionMismatch,

        /// <summary>Two orders were minted with the same number. Should be unreachable.</summary>
        NumberCollision,

        /// <summary>The key was taken but the winning order could not be read. Should be unreachable.</summary>
        ReplayLost,
    }

    /// <summary>
    /// The outcome of one placement attempt. A single record with optional payloads rather than a
    /// hierarchy: it never leaves this file, and the switch that consumes it is twenty lines below
    /// the switch that produces it.
    /// </summary>
    private sealed record Placement(
        PlacementStatus Status,
        Order? Order = null,
        IReadOnlyList<CheckoutPriceChange>? PriceChanges = null,
        CheckoutStockShortfall? Shortfall = null,
        string? Detail = null);
}

/// <summary>
/// Mints and reads the signed token that reopens one order without a session.
/// <para>
/// Data Protection, the same provider that seals the session cookie, under a <em>different purpose
/// string</em>. Purposes isolate ciphertexts from one another, so a session cookie can never be
/// replayed as an order token and an order token can never be replayed as a session — which is the
/// property that lets these two credentials coexist without one becoming a way to forge the other.
/// The <c>.v1</c> is the upgrade seam: changing what the payload contains means bumping it, which
/// invalidates every old token by construction rather than by hoping a parser stays compatible.
/// </para>
/// <para>
/// The payload is the order's id and nothing else. Not the session — binding one would defeat the
/// point, since the whole reason the link exists is to work for a visitor who has lost or never had
/// that cookie.
/// </para>
/// <para>
/// <strong>The known cost of putting it in the query string.</strong> A capability in a URL is
/// carried into browser history, into <c>Referer</c> headers on outbound links, and into any access
/// log that records query strings — which is why the endpoints here send <c>Cache-Control:
/// no-store</c> and why the token expires rather than living forever. It is in the query string
/// because a link has to be a link: a header-borne token cannot be pasted into an email, which is
/// the entire use case. A store handling more than a demo's stakes would send the token once, set a
/// short-lived scoped cookie from it and redirect to a clean URL, so the secret leaves the address
/// bar after the first hop.
/// </para>
/// </summary>
internal static class OrderRetrievalToken
{
    private const string ProtectorPurpose = "VelaCommerce.OrderRetrieval.v1";

    /// <summary>
    /// Issues a token for an order. Time-limited, and the limit lives inside the protected payload
    /// so it is checked by the server on every use rather than being a claim the holder could edit.
    /// </summary>
    public static string Issue(IDataProtectionProvider provider, Guid orderId) =>
        Protector(provider).Protect(orderId.ToString("N"), CheckoutPolicy.RetrievalLinkLifetime);

    /// <summary>
    /// Reads a token, or reports that there isn't a usable one.
    /// <para>
    /// Absent, truncated, edited, expired, or encrypted under a key that no longer exists all land
    /// in the same place: <see langword="false"/>, and the caller falls back to asking whether the
    /// order belongs to the current session. This never throws and never trusts the input, because
    /// the input is a string an attacker fully controls. A tampered token is not a 500; it is simply
    /// not a token.
    /// </para>
    /// </summary>
    public static bool TryRead(IDataProtectionProvider provider, string? token, out Guid orderId)
    {
        orderId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            return Guid.TryParseExact(Protector(provider).Unprotect(token), "N", out orderId)
                   && orderId != Guid.Empty;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
}
