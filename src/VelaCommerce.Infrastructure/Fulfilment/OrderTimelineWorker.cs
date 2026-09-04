using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Infrastructure.Fulfilment;

/// <summary>
/// Walks paid orders through the rest of their lifecycle on a demo clock: Paid becomes Packed
/// after a short dwell, Packed becomes Shipped after a slightly longer one, and shipping is where
/// the reserved units finally leave the warehouse.
///
/// <para>
/// This is the most visible thing in the demo — a reviewer watches an order they just placed cross
/// four states in about a minute — which is exactly why it is written defensively. A background
/// job that quietly invents a transition, ships the same parcel twice or drives a stock counter
/// negative would be discovered by the person it was built to impress.
/// </para>
///
/// <para><b>Four rules, each of which the obvious implementation gets wrong.</b></para>
///
/// <para>
/// <b>1. The state machine is the authority, not this worker.</b> Every move goes through
/// <see cref="Order.MarkPacked"/> or <see cref="Order.MarkShipped"/>; nothing here assigns
/// <c>Status</c> and no SQL here updates the <c>status</c> column. That matters more than it
/// looks: <c>OrderStateMachine</c> has no self-transitions on purpose, so an order that is already
/// Shipped <em>throws</em> rather than silently shipping again. A worker that wrote the column
/// directly would have thrown that alarm away and replaced it with a second stock deduction.
/// </para>
///
/// <para>
/// <b>2. Due-ness is derived from <c>PaidAt</c>, for both steps.</b> There is no <c>packed_at</c>
/// column, and this phase's single migration belongs to the outbox slice, so the ship deadline is
/// <c>PaidAt + PaidDwell + PackedDwell</c> rather than "a dwell after the row turned Packed". See
/// <see cref="OrderTimelineOptions.ElapsedSincePaidBefore"/> for why that is a better answer than
/// the column would have been, and for the one behaviour it changes.
/// </para>
///
/// <para>
/// <b>3. An order is claimed before it is moved.</b> Each transition runs in its own transaction
/// that begins by re-selecting the order <c>FOR UPDATE SKIP LOCKED</c> with the status it is
/// expected to be in. Without that claim, two replicas — or one replica whose previous sweep
/// overran — both read a Packed order, both ship it, and the stock ledger loses twice as many
/// units as the order contained. The read that finds work and the write that does it are
/// separated by an HTTP-free but still real window, and a claim is what closes it. Note that this
/// is a lock, not a "processing" flag: a flag survives the crash of the process that set it and
/// strands the order, a lock does not.
/// </para>
///
/// <para>
/// <b>4. Stock leaves by a guarded UPDATE, never by read-then-write.</b> Shipping mirrors
/// <see cref="StockItem.Ship"/> — on-hand and reserved both fall — but issues it as one
/// conditional statement per reservation, the same discipline the checkout uses to reserve and
/// the reaper uses to release. The guard is what makes a double-ship impossible to express rather
/// than merely unlikely: an UPDATE that requires <c>reserved &gt;= q</c> cannot produce a negative
/// counter, and <c>ck_stock_items_reserved_non_negative</c> is standing behind it either way.
/// </para>
///
/// <para>
/// <b>On tenancy.</b> The order query says
/// <c>IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])</c> for the same reason
/// <c>ReservationReaper</c> does: a background service has no visitor, the filter fails closed,
/// and an unfiltered read here would see nothing at all and do nothing very convincingly. The
/// <c>SoftDelete</c> filter is deliberately left on — a deleted order is not a shipment waiting to
/// happen.
/// </para>
/// </summary>
public sealed class OrderTimelineWorker(
    IServiceScopeFactory scopeFactory,
    OrderTimelineOptions options,
    TimeProvider timeProvider,
    ILogger<OrderTimelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "The order timeline is disabled by configuration ({Key}). Paid orders will stay Paid until a "
                + "worker runs.",
                $"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.Enabled)}");

            return;
        }

        logger.LogInformation(
            "The order timeline is running: Paid to Packed after {PaidDwell}, Packed to Shipped after a further "
            + "{PackedDwell}, swept every {SweepInterval}.",
            options.PaidDwell,
            options.PackedDwell,
            options.SweepInterval);

        if (options.OutlastsTheReservationWindow)
        {
            // Said once, at startup, rather than per sweep. It is a configuration smell, not an
            // error: see OrderTimelineOptions.OutlastsTheReservationWindow for the interaction.
            logger.LogWarning(
                "The configured timeline ({Total}) is at least as long as the reservation window ({Window}). An "
                + "order that is Paid or Packed when the window closes is invisible to the reaper, which only "
                + "sweeps orders still Pending - so its units stay reserved until the parcel ships rather than "
                + "being reclaimed. Nothing breaks; stock is simply promised for longer than the window "
                + "advertises. Shorten {PaidKey}/{PackedKey}, or lengthen CheckoutPolicy.ReservationWindow.",
                options.PaidDwell + options.PackedDwell,
                CheckoutPolicy.ReservationWindow,
                $"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.PaidDwell)}",
                $"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.PackedDwell)}");
        }

        // A sweep on boot, like the reaper and the dispatcher: a process that restarted mid-demo
        // is the case that leaves orders sitting at a state they are already overdue to leave.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Never let one bad sweep kill the service. The orders are still in the table with
                // their PaidAt intact, so the next tick recomputes exactly the same work.
                logger.LogError(exception, "An order timeline sweep failed. Retrying at the next interval.");
            }

            try
            {
                await Task.Delay(options.SweepInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Advances up to <see cref="OrderTimelineOptions.BatchSize"/> due orders by exactly one step
    /// each, one transaction per order. Public so a test can drive a sweep without waiting for a
    /// timer.
    /// <para>
    /// One step per order per sweep, deliberately. An order that is overdue for both moves is
    /// packed now and shipped on the next tick, which keeps every transaction small, keeps the
    /// Packed state visible even when catching up, and means the batch limit bounds work rather
    /// than orders.
    /// </para>
    /// </summary>
    /// <returns>What the sweep did, for logging and for tests to assert on.</returns>
    public async Task<OrderTimelineSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        // One scope per sweep, like the reaper: the context is scoped, and a background service
        // that resolved one from the root provider would keep a single change tracker and a single
        // connection alive for the life of the process.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();

        var now = timeProvider.GetUtcNow();

        var due = await FindDueAsync(db, now, cancellationToken);

        var result = OrderTimelineSweepResult.Empty;

        foreach (var candidate in due)
        {
            result = result.Add(await AdvanceAsync(db, candidate.Id, candidate.Step, cancellationToken));
        }

        if (result.Advanced > 0)
        {
            logger.LogInformation(
                "Order timeline sweep: {Packed} packed, {Shipped} shipped, {Units} unit(s) left the warehouse.",
                result.Packed,
                result.Shipped,
                result.UnitsShipped);
        }

        return result;
    }

    /// <summary>
    /// Finds the orders whose dwell has elapsed, oldest payment first, as ids and nothing else.
    /// <para>
    /// A projection rather than whole aggregates: most sweeps find nothing, and of what they do
    /// find, some will be claimed by another worker. Materialising an order and its lines to
    /// discover that is work spent on a row this sweep will not touch. The aggregate is loaded
    /// inside the claim, under the lock, which is the only point at which its contents can be
    /// trusted anyway.
    /// </para>
    /// <para>
    /// Both steps are found by one query rather than two. The predicate reads as the timeline
    /// itself — a Paid order past its pack deadline, or a Packed order past its ship deadline —
    /// and the cutoffs are computed once, in .NET, so PostgreSQL compares a column against a
    /// parameter instead of evaluating <c>paid_at + interval</c> per row.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<DueOrder>> FindDueAsync(
        VelaCommerceDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var packCutoff = options.LatestPaidAtDueBy(OrderTimelineStep.Pack, now);
        var shipCutoff = options.LatestPaidAtDueBy(OrderTimelineStep.Ship, now);

        var due = await db.Orders
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Where(order =>
                (order.Status == OrderStatus.Paid && order.PaidAt != null && order.PaidAt <= packCutoff)
                || (order.Status == OrderStatus.Packed && order.PaidAt != null && order.PaidAt <= shipCutoff))
            .OrderBy(order => order.PaidAt)
            .Select(order => new { order.Id, order.Status })
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        return due
            .Select(static candidate => new DueOrder(
                candidate.Id,
                candidate.Status is OrderStatus.Paid ? OrderTimelineStep.Pack : OrderTimelineStep.Ship))
            .ToList();
    }

    /// <summary>
    /// Claims one order and moves it one step, all inside one transaction.
    /// <para>
    /// Wrapped in the execution strategy because the context is configured with
    /// <c>EnableRetryOnFailure</c>, and a retrying strategy refuses a user-initiated transaction
    /// unless the whole transaction is handed to it — it has to be able to run the entire unit
    /// again. Each attempt therefore starts from a cleared change tracker and re-claims from
    /// scratch, so a retry can never apply a transition against a stale read. Clearing also keeps
    /// one order's entities out of the next order's <c>SaveChanges</c>, since the context is
    /// shared across the batch.
    /// </para>
    /// </summary>
    private async Task<OrderTimelineSweepResult> AdvanceAsync(
        VelaCommerceDbContext db,
        Guid orderId,
        OrderTimelineStep step,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async (CancellationToken token) =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var expected = step is OrderTimelineStep.Pack ? OrderStatus.Paid : OrderStatus.Packed;

                var order = await ClaimAsync(db, orderId, expected, token);

                if (order is null)
                {
                    // Either another worker holds it, or it is no longer in the status this sweep
                    // read. Both are ordinary — a shopper may have cancelled it between the two
                    // queries — and both mean there is nothing for this transaction to do.
                    await transaction.RollbackAsync(token);
                    return OrderTimelineSweepResult.Empty;
                }

                var shipped = step is OrderTimelineStep.Ship
                    ? await ShipReservedUnitsAsync(db, order, token)
                    : 0;

                // The state machine, not this worker. Pack throws unless the order is Paid; Ship
                // throws unless it is Packed; neither has a self-transition, so a replay is an
                // exception rather than a second shipment.
                if (step is OrderTimelineStep.Pack)
                {
                    order.MarkPacked();
                }
                else
                {
                    order.MarkShipped();
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                logger.LogInformation(
                    "Order {OrderNumber} advanced to {Status}{Units}.",
                    order.OrderNumber,
                    order.Status,
                    step is OrderTimelineStep.Ship ? $", releasing {shipped} unit(s) of stock" : string.Empty);

                return step is OrderTimelineStep.Pack
                    ? new OrderTimelineSweepResult(Packed: 1, Shipped: 0, UnitsShipped: 0)
                    : new OrderTimelineSweepResult(Packed: 0, Shipped: 1, UnitsShipped: shipped);
            },
            cancellationToken);
    }

    /// <summary>
    /// Takes the order and holds it for the length of this transaction, but only if it is still in
    /// the status the sweep expected.
    /// <para>
    /// Raw SQL, because this is a statement EF cannot express and must not rewrite.
    /// <c>FOR UPDATE SKIP LOCKED</c> is the claim: a second worker scanning the same row skips it
    /// rather than queuing behind a transaction it will lose to anyway. The status is part of the
    /// claim rather than checked afterwards in C#, so "is it still Packed?" is answered by the
    /// same locked read that takes the row — checking it after the lock would be correct too, but
    /// checking it before the lock, which is the natural thing to write, is not.
    /// </para>
    /// <para>
    /// <b><c>IgnoreQueryFilters</c> with no arguments, and it is load-bearing.</b> Leave the
    /// filters on and EF does two things to this statement, both verified against PostgreSQL by
    /// printing the generated SQL. It wraps the whole thing in a subquery, burying the locking
    /// clause — which is how a claim quietly stops being a claim. And then, because a background
    /// service has no demo session and the tenancy filter fails closed, it folds the outer
    /// predicate to a literal <c>WHERE FALSE</c>: the row is locked and then thrown away, so the
    /// worker claims nothing, ever, and the timeline silently never moves. Selective
    /// <c>IgnoreQueryFilters([DemoTenancyFilter])</c> — the right call for the ordinary query
    /// above — is not enough here, because the surviving <c>SoftDelete</c> filter still causes the
    /// wrap. Nothing is lost by dropping both: <c>deleted_at IS NULL</c> is written into the SQL,
    /// where PostgreSQL evaluates it as part of the locked read.
    /// </para>
    /// <para>
    /// The status travels as a parameter rather than a literal so <see cref="OrderStatus"/> stays
    /// the single source of that number, and the set is mapped, so the row comes back tracked and
    /// the transition below is an ordinary <c>SaveChanges</c>.
    /// </para>
    /// </summary>
    private static async Task<Order?> ClaimAsync(
        VelaCommerceDbContext db,
        Guid orderId,
        OrderStatus expected,
        CancellationToken cancellationToken)
    {
        var status = (int)expected;

        var claimed = await db.Orders
            .FromSql(
                $"""
                 SELECT *
                 FROM orders
                 WHERE id = {orderId}
                   AND status = {status}
                   AND deleted_at IS NULL
                 FOR UPDATE SKIP LOCKED
                 """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        return claimed.Count == 0 ? null : claimed[0];
    }

    /// <summary>
    /// Moves the order's reserved units out of the warehouse: on-hand and reserved both fall by
    /// the reserved quantity, one guarded statement per reservation.
    ///
    /// <para>
    /// <b>The reservations are the source of truth here, not the order lines.</b> A line says what
    /// the shopper bought; a reservation says what the ledger is actually holding on their behalf,
    /// and the two can differ — a lapsed reservation the reaper already released is exactly that
    /// case. Shipping what was reserved is what keeps <c>reserved</c> and <c>on_hand</c> falling
    /// by the same amount, which is the invariant that stops
    /// <c>ck_stock_items_reserved_within_on_hand</c> being violated from the other direction.
    /// Released reservations are skipped: those units went back on sale and may already belong to
    /// somebody else.
    /// </para>
    ///
    /// <para>
    /// <b>The statement is <see cref="StockItem.Ship"/> expressed as SQL,</b> for the reason the
    /// checkout gives at length: the domain method states the rule correctly but evaluates it
    /// against an in-memory copy, and only the database can compare and decrement in the same
    /// locked instant. The guard is doubled — <c>reserved &gt;= q</c> and <c>on_hand &gt;= q</c> —
    /// even though the table's own constraint makes the second redundant, because the redundancy
    /// costs nothing and states the intent for whoever reads the SQL without the schema open.
    /// </para>
    ///
    /// <para>
    /// <b>Ordered by variant id,</b> matching the order in which checkout takes the same rows. Two
    /// transactions touching an overlapping set of variants in a consistent order queue; in
    /// opposite orders they deadlock, and PostgreSQL resolves that by aborting one of them.
    /// </para>
    ///
    /// <para>
    /// A row count of zero is a warning, not a failure. It means the ledger no longer holds what
    /// this reservation claims — the reaper released it after the window closed, most likely — and
    /// failing the shipment over it would leave the order stuck at Packed forever, which is worse
    /// and less informative than a shipped order and a log line saying the stock was already gone.
    /// </para>
    /// </summary>
    /// <returns>Units that actually left the warehouse.</returns>
    private async Task<int> ShipReservedUnitsAsync(
        VelaCommerceDbContext db,
        Order order,
        CancellationToken cancellationToken)
    {
        var claims = await db.StockReservations
            .Where(reservation =>
                reservation.OrderId == order.Id && reservation.Status != ReservationStatus.Released)
            .OrderBy(reservation => reservation.VariantId)
            .ToListAsync(cancellationToken);

        var shipped = 0;

        foreach (var reservation in claims)
        {
            var moved = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_items
                 SET on_hand = on_hand - {reservation.Quantity},
                     reserved = reserved - {reservation.Quantity}
                 WHERE variant_id = {reservation.VariantId}
                   AND deleted_at IS NULL
                   AND reserved >= {reservation.Quantity}
                   AND on_hand >= {reservation.Quantity}
                 """,
                cancellationToken);

            if (moved != 1)
            {
                logger.LogWarning(
                    "Order {OrderNumber} shipped {Quantity} of variant {VariantId}, but the ledger no longer "
                    + "held them. Shipping anyway; on-hand is now overstated by that much for this variant.",
                    order.OrderNumber,
                    reservation.Quantity,
                    reservation.VariantId);

                continue;
            }

            shipped += reservation.Quantity;

            if (reservation.Status is ReservationStatus.Held)
            {
                // A Paid order whose reservations are still Held means something settled it
                // without confirming them — the webhook path, most plausibly. Confirming as we
                // ship takes the row out of the reaper's sweep, which would otherwise release
                // units that have physically left the building. Confirmed is the terminal state a
                // fulfilled reservation reaches in today's domain; see the note in the summary
                // about a Fulfilled state, which would say this more plainly but is not this
                // slice's to add.
                reservation.Confirm();
            }
        }

        return shipped;
    }

    /// <summary>One row of the sweep's worklist: which order, and which edge it is due to cross.</summary>
    private readonly record struct DueOrder(Guid Id, OrderTimelineStep Step);
}

/// <summary>
/// The tally for one sweep. Returned rather than logged only, so an integration test can assert on
/// what a sweep did without reading log output.
/// </summary>
/// <param name="Packed">Orders moved from Paid to Packed.</param>
/// <param name="Shipped">Orders moved from Packed to Shipped.</param>
/// <param name="UnitsShipped">Units removed from on-hand by those shipments.</param>
public readonly record struct OrderTimelineSweepResult(int Packed, int Shipped, int UnitsShipped)
{
    public static OrderTimelineSweepResult Empty => default;

    /// <summary>Orders that crossed an edge, whichever edge it was.</summary>
    public int Advanced => Packed + Shipped;

    /// <summary>Accumulates one order's outcome. An unclaimed order contributes nothing.</summary>
    public OrderTimelineSweepResult Add(OrderTimelineSweepResult other) => new(
        Packed + other.Packed,
        Shipped + other.Shipped,
        UnitsShipped + other.UnitsShipped);
}
