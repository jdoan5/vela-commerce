// THE ONE ENDPOINT IN THIS APPLICATION WHOSE JOB IS TO DELETE THINGS, AND THE FOUR THINGS THAT
// KEEP IT FROM DELETING THE WRONG ONES.
//
// 1. THE SESSION IS NEVER AN INPUT. It comes from ICurrentDemoSession, which is bound from a
//    signed cookie at the edge. There is no route parameter, no body, no header and no query
//    string here — nothing a caller can write that changes whose data is removed. An endpoint
//    that accepted "which session?" would be a delete-anybody button with a polite name.
//
// 2. THE SET OF ROWS IS PRODUCED BY THE TENANCY FILTER, NOT BY A WHERE CLAUSE THIS FILE WROTE.
//    Every statement below starts from db.Carts or db.Orders, which the DemoTenancy query filter
//    has already narrowed to one visitor. That filter reads "there is a session AND the row
//    belongs to it", so a request that somehow arrived without one resolves to WHERE FALSE and
//    this endpoint deletes nothing at all. The failure direction is the whole design: a reset
//    that runs against no session must be a no-op, never a truncate. There is exactly ONE
//    statement here that stands outside the filter — the locking read in ReleaseHeldStockAsync,
//    which has to, for a reason measured against PostgreSQL rather than assumed — and it is
//    handed only ids the filter itself produced and then re-checks the owner of every row it
//    got back. Both halves are argued in full at that method.
//
// 3. IT REFUSES BEFORE IT RELIES ON THAT. Point 2 is the net; the guard at the top of
//    ResetAsync is the floor. A host composed without the session middleware gets a loud 500
//    rather than a quiet, successful reset of nothing — because a silent success would hide the
//    misconfiguration until the day the filter changed.
//
// 4. IT NEVER TOUCHES THE CATALOG. Products, variants and stock_items rows are shared, so
//    nothing here deletes one. The only shared table this endpoint writes is stock_items, and it
//    writes it in exactly one direction — giving reserved units back — through the same guarded
//    conditional UPDATE the checkout, the reaper and the timeline worker use.
//
// There is deliberately no "reset everything" variant, not even behind a flag. The moment such a
// path exists, the tenancy filter stops being the only way to answer "whose rows?" and this file
// becomes a thing to audit rather than a thing to read.

using System.Diagnostics;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using VelaCommerce.Api.Hosting;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Registration for the demo's self-service reset: one visitor putting their own corner of a
/// shared demo back to how they found it.
/// <para>
/// This exists because the demo is a single deployment that strangers share and nobody supervises.
/// A reviewer who declines a card, abandons a checkout and fills a cart needs a way to start over
/// without waiting for a nightly job, and the shop needs a way to reclaim the stock those attempts
/// are holding. Both are the same button.
/// </para>
/// </summary>
public static class DemoEndpoints
{
    /// <summary>
    /// Order states whose units the stock ledger is still holding, and which therefore have to be
    /// handed back when the order is removed.
    /// <para>
    /// This used to be a private array right here, with the argument for its two omissions written
    /// beside it. It moved to <see cref="OrderStateMachine.HoldingStock"/> when
    /// <c>DemoSessionPurge</c> became the second caller needing the same three states for the same
    /// reason — the reasoning moved with it, and shipping being absent from the list is still the
    /// subtle half.
    /// </para>
    /// </summary>
    private static IReadOnlyList<OrderStatus> StatesHoldingStock => OrderStateMachine.HoldingStock;

    /// <summary>
    /// Maps the demo group. Called by the host, so this file never learns how the application is
    /// composed.
    /// </summary>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var demo = app
            .MapGroup("/api/demo")
            .WithTags("Demo")
            .AddEndpointFilter(PreventSharedCachingAsync);

        demo.MapPost("/reset", ResetAsync)
            .WithName("ResetDemoData")
            .WithSummary("Delete the calling visitor's own demo data")
            .WithDescription(
                "Removes this visitor's carts and orders and hands their reserved stock back to "
                + "the catalog. Scope is decided entirely by the signed session cookie: there is "
                + "no parameter naming a session, so a caller can only ever reset themselves. The "
                + "catalog, the stock on hand and every other visitor's data are untouched. "
                + "Reservations belonging to an order that has already shipped are left alone - "
                + "those units are gone, not held - and reservations already released are ignored. "
                + "Answers 200 with a count of everything removed, including when there was "
                + "nothing to remove, so the button is safe to press twice.")
            .Produces<DemoResetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    /// <summary>
    /// Marks the reset response uncacheable by anything that is not this browser, for the reason
    /// the cart and checkout groups give: the answer describes one visitor's data and the request
    /// carries a session cookie, so a shared cache in front of the demo could otherwise hand one
    /// person's tally — and, with the cookie, their session — to the next.
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

    /// <summary>
    /// Releases the visitor's held stock and deletes their carts and orders, in one transaction.
    /// <para>
    /// <strong>Hard delete, not soft.</strong> Everywhere else in this system a row is kept and
    /// hidden; here it is removed, for three reasons that all point the same way. "Reset my demo
    /// data" is a promise about the data, and a row that still exists has not been reset. The
    /// per-session row caps in <c>DemoQuotas</c> count rows, so a reset that only hid them would
    /// leave the visitor exactly as stuck as before. And <c>ux_orders_demo_session_id_idempotency_key</c>
    /// is deliberately unfiltered — a soft-deleted order keeps its idempotency key spent, so a
    /// reviewer who reset and then replayed the <c>Duplicate</c> payment scenario with the same key
    /// would be handed back a replay of an order they can no longer see, which is the most
    /// confusing possible outcome of pressing a button labelled "start over".
    /// </para>
    /// <para>
    /// <strong>Undelivered settlements are left alone.</strong> An order removed here may still
    /// have an outbox row queued for it. That is safe and is not worth reaching into the outbox to
    /// tidy: the settlement receiver already answers 200 for an event whose order no longer
    /// exists, because "the order is gone" is not the sender's fault and is not fixed by retrying.
    /// The outbox has no order id column to filter on either, so deleting those rows would mean
    /// parsing payloads — a coupling worth more than the two rows it would save.
    /// </para>
    /// </summary>
    private static async Task<Results<Ok<DemoResetResponse>, ProblemHttpResult>> ResetAsync(
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        CancellationToken cancellationToken)
    {
        // The floor under the query filter's net. Unreachable in the composed host, where the
        // session middleware runs before any endpoint — which is exactly why it is written down.
        if (session.SessionId is not { } sessionId)
        {
            return NoDemoSessionProblem();
        }

        var started = Stopwatch.GetTimestamp();

        // Wrapped in the execution strategy because the context is configured with
        // EnableRetryOnFailure, and a retrying strategy refuses a user-initiated transaction unless
        // it owns the whole unit of work — it has to be able to run the entire thing again. Every
        // attempt therefore re-reads from scratch, so a retry can never release stock twice: the
        // conditional claims below are re-evaluated against whatever the previous attempt committed,
        // which was nothing.
        var strategy = db.Database.CreateExecutionStrategy();

        var summary = await strategy.ExecuteAsync(
            async (CancellationToken token) =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var tally = await RemoveEverythingOwnedByThisSessionAsync(db, sessionId, token);

                await transaction.CommitAsync(token);

                return tally;
            },
            cancellationToken);

        return TypedResults.Ok(summary with
        {
            // Stopwatch measures a duration rather than reading a clock, which is why it is not
            // one of the ambient-clock reads the architecture test bans. Reported because the demo
            // makes a claim about this endpoint being fast and a number is cheaper than trust.
            ElapsedMilliseconds = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        });
    }

    /// <summary>
    /// The body of the reset, inside the caller's transaction.
    /// <para>
    /// The order of operations is not arbitrary. Stock is handed back <em>before</em> anything is
    /// deleted, because the reservation rows are the only record of how much to hand back; delete
    /// them first and the units are stranded with no way to discover them. Reservations go before
    /// orders because a reservation whose order has been deleted is unreachable through the tenancy
    /// filter — it carries no session id of its own — and would never be swept again.
    /// </para>
    /// </summary>
    private static async Task<DemoResetResponse> RemoveEverythingOwnedByThisSessionAsync(
        VelaCommerceDbContext db,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        // TENANCY HAPPENS HERE, ONCE. No WHERE demo_session_id = ... anywhere in this method: the
        // DemoTenancy filter writes it, and everything below is scoped to the ids this query
        // returned. A regression in the filter therefore shows up as "the reset deletes nothing",
        // never as "the reset deletes somebody else's orders".
        var orders = await db.Orders
            .AsNoTracking()
            .Select(order => new { order.Id, LineCount = order.Lines.Count })
            .ToListAsync(cancellationToken);

        var orderIds = orders.Select(order => order.Id).ToArray();
        var orderLines = orders.Sum(order => order.LineCount);

        var released = orderIds.Length == 0
            ? StockReleased.Nothing
            : await ReleaseHeldStockAsync(db, sessionId, orderIds, cancellationToken);

        // Scoped to the ids above rather than to the filter, so a checkout that committed while
        // this transaction was running keeps its order AND its reservations. Deleting an order
        // whose stock this pass never accounted for is precisely how units get stranded.
        var reservationsRemoved = orderIds.Length == 0
            ? 0
            : await db.StockReservations
                .Where(reservation => orderIds.Contains(reservation.OrderId))
                .ExecuteDeleteAsync(cancellationToken);

        // ExecuteDelete applies the entity's query filters, so this statement carries DemoTenancy
        // as well as the id list — belt and braces, and the belt is the one that would still hold
        // if the id list were ever computed wrongly. order_lines go with it: the foreign key is
        // ON DELETE CASCADE in PostgreSQL, not merely in the change tracker.
        var ordersRemoved = orderIds.Length == 0
            ? 0
            : await db.Orders
                .Where(order => orderIds.Contains(order.Id))
                .ExecuteDeleteAsync(cancellationToken);

        // Carts hold no stock and reference nothing, so they need no accounting pass and are
        // deleted straight through the filter. A cart created by a concurrent add during this
        // transaction is this visitor's too, and removing it is what they asked for.
        var cartLines = await db.Carts
            .AsNoTracking()
            .Select(cart => cart.Lines.Count)
            .ToListAsync(cancellationToken);

        var cartsRemoved = await db.Carts.ExecuteDeleteAsync(cancellationToken);

        // The admin's price overrides are this visitor's too, and a reset that left them behind
        // would hand somebody a "fresh" shop still quietly marked down. Not counted on the response:
        // DemoResetResponse is a fixed contract the committed OpenAPI document and the Bruno
        // collection both assert against, and this is not worth widening it for.
        await db.ClearOverridesAsync(cancellationToken);

        return new DemoResetResponse(
            CartsRemoved: cartsRemoved,
            CartLinesRemoved: cartLines.Sum(),
            OrdersRemoved: ordersRemoved,
            OrderLinesRemoved: orderLines,
            ReservationsRemoved: reservationsRemoved,
            ReservationsReleased: released.Reservations,
            UnitsReturnedToStock: released.Units,
            ElapsedMilliseconds: 0);
    }

    /// <summary>
    /// Gives back the units this visitor's unfinished orders are holding on the shared ledger.
    /// <para>
    /// <strong>Two races, two different defences, and neither one covers the other.</strong>
    /// </para>
    /// <para>
    /// <em>Against <c>OrderTimelineWorker</c>:</em> the orders are claimed with
    /// <c>SELECT … FOR UPDATE</c> first, and their status is read <em>after</em> the lock rather
    /// than before it. Shipping does not change a reservation's status — it decrements the ledger
    /// and leaves the row Confirmed — so no amount of care about reservation rows would notice a
    /// shipment landing mid-reset. Holding the order row is what makes "is this order still
    /// holding stock?" an answer that stays true for the length of this transaction: the worker
    /// claims with <c>FOR UPDATE SKIP LOCKED</c>, so it steps over an order this transaction holds
    /// instead of racing it. Locks are taken in id order, and orders-then-reservations is the same
    /// sequence the timeline worker uses, so the two cannot deadlock against each other.
    /// </para>
    /// <para>
    /// <em>Against <c>ReservationReaper</c> and the settlement receiver:</em> each reservation is
    /// moved to Released by a conditional UPDATE that names the status it was observed in. Only one
    /// actor can win that statement, and the loser learns it from a row count of zero rather than
    /// from an exception. This is the same claim-then-act discipline the reaper uses in reverse,
    /// and it is what stops two processes both deciding they are the one giving a unit back. The
    /// reaper takes the order row first and its reservations second — the same sequence this method
    /// uses — so the cycle this paragraph used to warn about is closed. The claims are still
    /// re-evaluated on every attempt rather than cached, because the execution strategy may retry
    /// for other reasons: a serialization failure, or a connection lost mid-transaction.
    /// </para>
    /// <para>
    /// The ledger write is <c>StockItem.Release</c> expressed as SQL, for the reason the checkout
    /// gives at length: the domain method states the rule correctly but judges an in-memory copy,
    /// and only the database can compare and decrement in the same locked instant. A row count of
    /// zero there is a warning, not a failure — it means the ledger no longer holds what this
    /// reservation claims, most plausibly because the reaper released it a moment ago — and the
    /// reset still stands.
    /// </para>
    /// </summary>
    private static async Task<StockReleased> ReleaseHeldStockAsync(
        VelaCommerceDbContext db,
        Guid sessionId,
        Guid[] orderIds,
        CancellationToken cancellationToken)
    {
        // THE ONE IgnoreQueryFilters IN THIS FILE, AND WHY IT IS NOT OPTIONAL.
        //
        // Written with the filters left on first, and it did not work: EF composes a filtered
        // FromSql by wrapping it in a subquery and projecting the outer columns from the property
        // path rather than the mapped column name, so `SELECT *` came back with `captured_amount`
        // and the outer query asked for `v.Captured_Amount`. PostgreSQL answered
        // `42703: column v.Captured_Amount does not exist`. That is a hard error rather than a
        // silent one — which is the good version of this discovery — but it means the filtered
        // form of this statement cannot exist at all. It is the same wrap ReservationReaper and
        // OrderTimelineWorker both document, reached from a different direction; and as
        // OrderTimelineWorker records, dropping only DemoTenancy is not enough, because the
        // surviving SoftDelete filter causes the wrap on its own. So both go, and `deleted_at IS
        // NULL` is written into the SQL where PostgreSQL evaluates it as part of the locked read.
        //
        // What replaces the filter is not trust, it is two things. The id list is the filter's own
        // output — it came from db.Orders a moment ago, with no WHERE clause written by hand — so
        // this statement can only name rows tenancy already selected. And every row that comes back
        // is checked against the session below, so if that list were ever wrong, this method
        // refuses to touch the shared stock ledger instead of quietly releasing somebody else's
        // reservations. An unfiltered read plus an explicit owner check is a stronger guarantee
        // than a filter would have been, because the check is on the rows rather than on the query.
        var locked = await db.Orders
            .FromSql(
                $"""
                 SELECT *
                 FROM orders
                 WHERE id = ANY({orderIds})
                   AND deleted_at IS NULL
                 ORDER BY id
                 FOR UPDATE
                 """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Defence in depth, in the shape CheckoutEndpoints uses before it writes an order's owner.
        // Unreachable unless the tenancy filter has stopped filtering, which is exactly the failure
        // this endpoint must not survive: releasing another visitor's reservations would put stock
        // that is genuinely sold back on sale, and no other check anywhere would notice. Thrown
        // rather than returned, because there is no sensible partial answer — the transaction rolls
        // back, nothing is deleted, and the 500 is the correct description of a server whose
        // isolation has failed.
        if (locked.Exists(order => order.DemoSessionId != sessionId))
        {
            throw new InvalidOperationException(
                "A demo reset loaded an order that does not belong to the calling session. The "
                + "DemoTenancy query filter produced the id list, so this cannot happen unless "
                + "that filter has stopped working. Nothing has been deleted or released.");
        }

        var holdingStock = locked
            .Where(order => StatesHoldingStock.Contains(order.Status))
            .Select(order => order.Id)
            .ToArray();

        if (holdingStock.Length == 0)
        {
            return StockReleased.Nothing;
        }

        var claims = await db.StockReservations
            .AsNoTracking()
            .Where(reservation =>
                holdingStock.Contains(reservation.OrderId)
                && reservation.Status != ReservationStatus.Released)
            // Same order the checkout takes these rows in, so two transactions touching an
            // overlapping set of variants queue rather than deadlock.
            .OrderBy(reservation => reservation.VariantId)
            .Select(reservation => new
            {
                reservation.Id,
                reservation.VariantId,
                reservation.Quantity,
                reservation.Status,
            })
            .ToListAsync(cancellationToken);

        var releasedReservations = 0;
        var releasedUnits = 0;

        foreach (var claim in claims)
        {
            var observed = (int)claim.Status;

            var won = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_reservations
                 SET status = {(int)ReservationStatus.Released}
                 WHERE id = {claim.Id}
                   AND status = {observed}
                   AND deleted_at IS NULL
                 """,
                cancellationToken);

            if (won != 1)
            {
                // Somebody else moved this reservation between the read and here. Its units are
                // theirs to account for; touching the ledger now would release them twice.
                continue;
            }

            releasedReservations++;

            var given = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_items
                 SET reserved = reserved - {claim.Quantity}
                 WHERE variant_id = {claim.VariantId}
                   AND deleted_at IS NULL
                   AND reserved >= {claim.Quantity}
                 """,
                cancellationToken);

            if (given == 1)
            {
                releasedUnits += claim.Quantity;
            }
        }

        return new StockReleased(releasedReservations, releasedUnits);
    }

    /// <summary>
    /// The fail-closed branch. 500 rather than 401 for the reason the cart endpoints give: there is
    /// no login to send anybody to, and a request that reaches here without a session means the
    /// host was composed without the session middleware, which is the server's fault. Answering 200
    /// with a row of zeros would be worse than either — technically true, and silent about a
    /// misconfiguration that has already broken every cart on the site.
    /// </summary>
    private static ProblemHttpResult NoDemoSessionProblem() =>
        TypedResults.Problem(
            title: "No demo session",
            detail: "This request has no demo session, so there is no visitor whose data could be "
                    + "reset. Nothing has been deleted. A reset refuses rather than guess whose "
                    + "rows it is looking at.",
            statusCode: StatusCodes.Status500InternalServerError);

    /// <summary>Units handed back to the shared ledger by one reset.</summary>
    /// <param name="Reservations">Reservation rows this pass moved to Released.</param>
    /// <param name="Units">Units the ledger actually gave back, which can be fewer if another actor got there first.</param>
    private readonly record struct StockReleased(int Reservations, int Units)
    {
        public static StockReleased Nothing => default;
    }
}

/// <summary>
/// What a reset removed, so the storefront can say so rather than claim it.
/// <para>
/// Every number is what the database reported, not what the request intended. That distinction is
/// the point of returning them at all: a banner that says "removed 2 orders and returned 3 units to
/// stock" is a demo explaining itself, while a banner that says "done" is a demo asking to be
/// trusted.
/// </para>
/// </summary>
/// <param name="CartsRemoved">Cart rows deleted. Normally one, occasionally more — the index on <c>demo_session_id</c> is deliberately not unique.</param>
/// <param name="CartLinesRemoved">Cart lines that went with them, as observed just before the delete.</param>
/// <param name="OrdersRemoved">Order rows deleted.</param>
/// <param name="OrderLinesRemoved">Order lines that went with them, as observed just before the delete.</param>
/// <param name="ReservationsRemoved">Reservation rows deleted, including ones that were already Released and therefore held nothing.</param>
/// <param name="ReservationsReleased">Reservations this reset was the one to release. Never more than <paramref name="ReservationsRemoved"/>.</param>
/// <param name="UnitsReturnedToStock">Units the shared ledger actually took back and put on sale again.</param>
/// <param name="ElapsedMilliseconds">How long the whole transaction took, measured server-side.</param>
public sealed record DemoResetResponse(
    int CartsRemoved,
    int CartLinesRemoved,
    int OrdersRemoved,
    int OrderLinesRemoved,
    int ReservationsRemoved,
    int ReservationsReleased,
    int UnitsReturnedToStock,
    int ElapsedMilliseconds)
{
    /// <summary>Whether anything at all was there to remove. Lets the banner say "nothing to reset" honestly.</summary>
    public bool RemovedAnything =>
        CartsRemoved > 0 || OrdersRemoved > 0 || ReservationsRemoved > 0;
}

/// <summary>
/// Refuses a write that would push one visitor past their row allowance, before the endpoint that
/// would have written it runs.
/// <para>
/// <strong>Why it lives in this file rather than beside the other demo-safety code in
/// <c>Api.Hosting</c>.</strong> Because it reads the database, and an architecture test confines
/// <c>VelaCommerceDbContext</c> to the persistence layer, the seeder, background services and this
/// namespace. That rule is right and it decided the placement: a quota that counts rows is a
/// transaction owner, and transaction owners live where the endpoints do. Written under
/// <c>Hosting/</c> first, it failed the rule before it ever failed a review — which is what the
/// rule is for.
/// </para>
/// <para>
/// <strong>Why a middleware and not an endpoint filter.</strong> A filter is the better shape and
/// is not available: the cart and checkout groups are composed in
/// <c>CartEndpoints.MapCartEndpoints</c> and <c>CheckoutEndpoints.MapCheckoutEndpoints</c>, which
/// this slice does not own, and adding one would mean two agents editing the same registration in
/// the same phase. A middleware placed after the session is bound reaches the same requests
/// without touching either file. The cost is that the routes are matched by method and path here
/// rather than inherited from the group, so <see cref="CartItemsPath"/> and
/// <see cref="CheckoutPath"/> have to be kept honest — they are the two literals in this file that
/// a route rename would silently break, which is why they are named constants and why the summary
/// for this slice calls them out.
/// </para>
/// <para>
/// <strong>Why not a database trigger.</strong> A trigger would be unbypassable, which is the
/// property this design gives up. It was rejected on cost of failure rather than on elegance: a
/// trigger raises inside whatever transaction is running, so hitting the order cap would abort a
/// checkout mid-flight and surface as a <c>DbUpdateException</c> in a handler that has carefully
/// enumerated the three <c>DbUpdateException</c>s it expects. Turning that into a clean 409 would
/// mean teaching every current and future writer about a quota. Refusing before the transaction
/// opens keeps the failure in one place and keeps it cheap.
/// </para>
/// <para>
/// The check is not atomic, and does not need to be: two adds racing past a cap of forty leave a
/// cart with forty-one lines, which is a rounding error against the thing being prevented. What it
/// must never do is refuse a legitimate shopper, which is why every count is read through the
/// tenancy filter and a caller with no session is waved through to the endpoint's own refusal.
/// </para>
/// </summary>
internal static class DemoQuotas
{
    /// <summary>The one cart route that can grow the row count. PATCH and DELETE only ever shrink it.</summary>
    private static readonly PathString CartItemsPath = new("/api/cart/items");

    /// <summary>The route that creates orders.</summary>
    private static readonly PathString CheckoutPath = new("/api/checkout");

    /// <summary>
    /// Applies the caps, or does nothing at all for a request that cannot create rows.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the request may proceed, or the refusal to send instead.
    /// </returns>
    public static async Task<IResult?> EvaluateAsync(
        HttpContext context,
        DemoQuotaOptions quotas,
        CancellationToken cancellationToken)
    {
        var request = context.Request;

        if (!HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        var addingToCart = Addresses(request.Path, CartItemsPath);
        var checkingOut = Addresses(request.Path, CheckoutPath);

        if (!addingToCart && !checkingOut)
        {
            return null;
        }

        // Resolved per request from the request's own scope, which is the same scope the endpoint
        // will resolve it from — so this is one extra query on an existing connection rather than a
        // second context. GetService rather than GetRequiredService: a host composed without
        // persistence has no rows to cap, and this middleware must not be the thing that breaks it.
        if (context.RequestServices.GetService<VelaCommerceDbContext>() is not { } db)
        {
            return null;
        }

        return addingToCart
            ? await EvaluateCartAsync(db, quotas, cancellationToken)
            : await EvaluateCheckoutAsync(db, quotas, cancellationToken);
    }

    /// <summary>
    /// Whether this request path addresses the given route.
    /// <para>
    /// A segment comparison rather than string equality, because ASP.NET Core's route matcher
    /// treats <c>/api/checkout/</c> as <c>/api/checkout</c> and is case-insensitive. A quota that
    /// compared strings would be bypassed by a trailing slash — the request would reach the
    /// endpoint and this middleware would have decided it was not interesting. Anything left over
    /// beyond a bare slash is a different route and is not ours to cap.
    /// </para>
    /// </summary>
    private static bool Addresses(PathString path, PathString route) =>
        path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase, out var remaining)
        && (!remaining.HasValue || remaining.Value is "/");

    /// <summary>
    /// Counts this visitor's carts and the lines in the one they are about to write to.
    /// <para>
    /// ONE query answers both questions. <c>OrderByDescending(cart.Id)</c> is not decoration: it is
    /// the same ordering <c>CartEndpoints.LoadCartForWriteAsync</c> uses to decide which cart "the
    /// cart" means — newest wins, and newest is expressible as id order because ids are UUIDv7 — so
    /// the first count in the list belongs to exactly the cart the add would land in. Any other
    /// ordering would cap a different cart from the one being written, which is the kind of bug
    /// that only appears for the visitor unlucky enough to own two.
    /// </para>
    /// <para>
    /// No <c>WHERE demo_session_id = ...</c>: the DemoTenancy filter supplies it. A caller with no
    /// session therefore counts zero and is allowed through — correctly, because the endpoint
    /// behind this refuses a session-less write itself, and a quota is not the right place to
    /// discover that the host is misconfigured.
    /// </para>
    /// </summary>
    private static async Task<IResult?> EvaluateCartAsync(
        VelaCommerceDbContext db,
        DemoQuotaOptions quotas,
        CancellationToken cancellationToken)
    {
        var lineCounts = await db.Carts
            .AsNoTracking()
            .OrderByDescending(cart => cart.Id)
            .Select(cart => cart.Lines.Count)
            .ToListAsync(cancellationToken);

        if (lineCounts.Count >= quotas.MaxCartsPerSession)
        {
            return Refusal(
                "Too many carts on this demo session",
                $"This session already owns {lineCounts.Count} cart(s), which is the demo's limit "
                + $"of {quotas.MaxCartsPerSession}. Nothing has been added.");
        }

        // A session with no cart yet is about to create one holding a single line, so there is
        // nothing to compare. The list is ordered newest-first, so index 0 is the cart the endpoint
        // will load.
        if (lineCounts.Count > 0 && lineCounts[0] >= quotas.MaxLinesPerCart)
        {
            return Refusal(
                "That cart is full",
                $"This cart already holds {lineCounts[0]} lines, which is the demo's limit of "
                + $"{quotas.MaxLinesPerCart} distinct items. Remove a line, or reset your demo "
                + "data. Changing the quantity of a line you already have is unaffected - the "
                + "limit is on distinct items, not on units.");
        }

        return null;
    }

    /// <summary>
    /// Counts this visitor's orders. One indexed count against
    /// <c>ix_orders_demo_session_id_placed_at</c>, and no session id written by hand.
    /// </summary>
    private static async Task<IResult?> EvaluateCheckoutAsync(
        VelaCommerceDbContext db,
        DemoQuotaOptions quotas,
        CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().CountAsync(cancellationToken);

        if (orders < quotas.MaxOrdersPerSession)
        {
            return null;
        }

        return Refusal(
            "That is enough orders for one demo session",
            $"This session has placed {orders} orders, which is the demo's limit of "
            + $"{quotas.MaxOrdersPerSession}. Your cart is untouched and nothing has been charged - "
            + "nothing here ever is.");
    }

    /// <summary>
    /// The one refusal shape, and the reason it is 409 rather than 429.
    /// <para>
    /// 429 says "you are going too fast, wait and try again", and waiting will not help here: the
    /// rows are already written and time does not remove them. 409 says the request conflicts with
    /// the state of the resource, which is exactly true, and it points at the one action that
    /// resolves it. Every message ends by naming <c>POST /api/demo/reset</c> because a limit a
    /// visitor cannot clear themselves is a dead end on a demo nobody is supervising.
    /// </para>
    /// </summary>
    private static IResult Refusal(string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail + " Press \"Reset my demo data\" in the banner, or POST /api/demo/reset, "
                    + "to clear this session's carts and orders and start over.",
            statusCode: StatusCodes.Status409Conflict);
}
