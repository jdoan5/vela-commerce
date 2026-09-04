using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// Returns stock that a checkout reserved but never paid for.
/// <para>
/// Without this the demo's headline invariant quietly stops being true. Checkout reserves
/// units before asking the gateway, and only two outcomes hand them back on their own: a
/// decline, and a failure inside the reservation transaction. A shopper who abandons at the
/// payment step, a gateway that never answers, or a settlement still in flight all leave units
/// <see cref="ReservationStatus.Held"/> — and <see cref="StockReservation.HasLapsed"/> had no
/// caller anywhere in the solution, so "held until the reservation lapses" meant held forever.
/// One abandoned checkout of the last unit would take a product off sale permanently.
/// </para>
/// <para>
/// <b>It works order by order, not reservation by reservation, and the order's status is the
/// authority.</b> A sweep cancels an order that is still Pending and releases every line that
/// order holds; it does not touch an order that has moved on. That distinction matters in one
/// direction in particular: a Paid order whose settlement failed to confirm its reservations
/// keeps them, because somebody bought those units, and handing them back would be an oversell.
/// The sweep used to release them.
/// </para>
/// <para>
/// It releases with the same guarded statement the checkout uses in reverse, so a double
/// release can never drive <c>reserved</c> below zero and trip the check constraint. Reads run
/// with the tenancy filter ignored on purpose: this is a background job with no visitor, and
/// the filter fails closed, so an unfiltered query here would see nothing and silently do
/// nothing at all.
/// </para>
/// </summary>
public sealed class ReservationReaper(
    IServiceScopeFactory scopeFactory,
    ReservationReaperOptions options,
    TimeProvider timeProvider,
    ILogger<ReservationReaper> logger) : BackgroundService
{
    /// <summary>How often to look. Well inside the reservation window, so a lapse is noticed promptly.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Checked here rather than inside SweepAsync, so a caller holding this object can still
        // drive one sweep deliberately — which is what the integration tests do. Off means "nothing
        // sweeps on a timer", not "sweeping is forbidden".
        if (!options.Enabled)
        {
            logger.LogInformation(
                "The reservation reaper is disabled by configuration ({Key}). Lapsed reservations will "
                + "keep holding their units until something sweeps.",
                $"{ReservationReaperOptions.SectionName}:{nameof(ReservationReaperOptions.Enabled)}");

            return;
        }

        // A first sweep on boot matters: a container that restarts mid-checkout is exactly the
        // case that strands units, and nothing else would notice until the next tick.
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
                // Never let one bad sweep kill the service. A reaper that dies silently is worse
                // than one that fails loudly and tries again in a minute.
                logger.LogError(exception, "A reservation sweep failed. Retrying at the next interval.");
            }

            try
            {
                await Task.Delay(SweepInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Returns the stock of every abandoned checkout it can reach, ONE ORDER PER TRANSACTION.
    /// <para>
    /// <b>Lock order: the order row first, then its reservations, then the ledger by variant.</b>
    /// That sequence is not a preference, it is the house convention — the settlement receiver, the
    /// timeline worker, the refund handler and the checkout all take these rows the same way round.
    /// This worker used to take reservations first and the orders second, and two writers taking
    /// the same two rows in opposite orders is a deadlock. It was reachable in exactly the case
    /// both pieces of code exist for, and was reproduced against the real receiver as PostgreSQL
    /// <c>40P01</c>, aborting the settlement — a 500 to the payment gateway from a receiver whose
    /// whole design is built never to send one.
    /// </para>
    /// <para>
    /// <b>One transaction per order, for the same reason the timeline worker uses one.</b> A single
    /// transaction for the whole batch would hold up to <see cref="ReservationReaperOptions.BatchSize"/>
    /// order rows locked while it worked through all of them, and the settlement receiver waits on
    /// that lock deliberately rather than skipping it — so a settlement for any order in the batch
    /// would queue behind the entire sweep instead of behind one order's worth of work. Committing
    /// per order also keeps each ledger write inside a transaction that touches one order's
    /// variants, which is what makes the variant ordering below sufficient.
    /// </para>
    /// <para>
    /// The candidate read that picks the work takes NO lock, which is what makes orders-first
    /// possible at all: the reaper cannot know which orders to lock until it has looked at
    /// reservations. Everything that read saw is re-checked under the lock, so a row that changed
    /// in between costs a wasted candidate and nothing else.
    /// </para>
    /// <para>
    /// Reads use bare <c>IgnoreQueryFilters()</c>: the tenancy filter fails closed and would blind a
    /// worker with no visitor, and the surviving soft-delete filter alone is enough to make EF wrap
    /// the statement and bury the locking clause.
    /// </para>
    /// </summary>
    /// <returns>Units actually returned to the stock ledger.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();

        var now = timeProvider.GetUtcNow();
        var candidates = await FindCandidatesAsync(db, now, cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var reclaimed = 0;
        var cancelled = 0;

        foreach (var orderId in candidates)
        {
            var (units, wasCancelled) = await ReapAsync(db, orderId, cancellationToken);

            reclaimed += units;

            if (wasCancelled)
            {
                cancelled++;
            }
        }

        if (cancelled > 0)
        {
            logger.LogInformation(
                "Reclaimed {Units} unit(s) and cancelled {Orders} abandoned order(s).",
                reclaimed,
                cancelled);
        }
        else
        {
            // Candidates but no cancellations means every one of them was taken by somebody else
            // between the unlocked read and the lock. Ordinary, and silent at Information — but a
            // sweep that finds work and does none should still be findable when it is not ordinary.
            logger.LogDebug(
                "Found {Candidates} order(s) with lapsed reservations and locked none of them.",
                candidates.Count);
        }

        return reclaimed;
    }

    /// <summary>
    /// The orders with at least one lapsed, still-held reservation. Takes no lock.
    /// <para>
    /// The join to <c>orders</c> is what stops this starving. A paid order whose settlement failed
    /// to confirm its reservations leaves them Held for good — correctly, because somebody bought
    /// those units — but they still match every reservation predicate here, so without the join
    /// such an order would be returned by every sweep for the life of the deployment and rejected
    /// by the locking step every time. Ids are UUIDv7 and sort by age, so once a batch's worth of
    /// them exists the oldest fill it permanently and no newer abandoned checkout is reached again.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> FindCandidatesAsync(
        VelaCommerceDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var held = (int)ReservationStatus.Held;
        var pending = (int)OrderStatus.Pending;

        return await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT DISTINCT reservation.order_id AS "Value"
                 FROM stock_reservations AS reservation
                 JOIN orders AS "order" ON "order".id = reservation.order_id
                 WHERE reservation.status = {held}
                   AND reservation.expires_at <= {now}
                   AND reservation.deleted_at IS NULL
                   AND "order".status = {pending}
                   AND "order".deleted_at IS NULL
                 ORDER BY reservation.order_id
                 LIMIT {options.BatchSize}
                 """)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Cancels one abandoned order and gives its units back, in one transaction.
    /// </summary>
    private async Task<(int Units, bool Cancelled)> ReapAsync(
        VelaCommerceDbContext db,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async Task<(int Units, bool Cancelled)> (CancellationToken token) =>
            {
                // Every attempt starts clean. The context is resolved once, outside this lambda, so
                // a retry reuses it — and without this the previous attempt's Cancel() is still
                // tracked as Modified, against a row this attempt has not re-claimed and holds no
                // lock on. The next SaveChanges would write it anyway. Every other
                // execution-strategy transaction in this solution clears here for the same reason.
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var pending = (int)OrderStatus.Pending;
                var held = (int)ReservationStatus.Held;

                // THE ORDER ROW FIRST. SKIP LOCKED, so an order somebody else is holding is left
                // for the next sweep rather than queued behind them: whoever holds the row owns the
                // decision about it, and if a settlement in flight does not take it out of Pending,
                // it is still here in a minute.
                var claimed = await db.Orders
                    .FromSql(
                        $"""
                         SELECT *
                         FROM orders
                         WHERE id = {orderId}
                           AND status = {pending}
                           AND deleted_at IS NULL
                         FOR UPDATE SKIP LOCKED
                         """)
                    .IgnoreQueryFilters()
                    .ToListAsync(token);

                if (claimed.Count == 0)
                {
                    await transaction.RollbackAsync(token);
                    return (0, false);
                }

                var order = claimed[0];

                // EVERY Held reservation, not only the lapsed ones: the order is about to be
                // cancelled, and leaving any line promised would strand those units on a shelf
                // nobody can sell from.
                //
                // ORDER BY variant_id joins the convention every other writer of stock_items
                // follows — checkout reserves its lines in variant order for exactly this reason,
                // and the refund handler releases in it. Reservation id is cart-insertion order and
                // uncorrelated with variant, so sorting by it let two sweeps take two ledger rows in
                // opposite orders and deadlock on stock_items — the same cycle one table down.
                var reservations = await db.StockReservations
                    .FromSql(
                        $"""
                         SELECT *
                         FROM stock_reservations
                         WHERE order_id = {orderId}
                           AND status = {held}
                           AND deleted_at IS NULL
                         ORDER BY variant_id, id
                         FOR UPDATE
                         """)
                    .IgnoreQueryFilters()
                    .ToListAsync(token);

                var units = 0;

                foreach (var reservation in reservations)
                {
                    // Guarded on status rather than trusting the object read a moment ago:
                    // StockReservation.Release() judges an in-memory copy and EF then emits an
                    // unguarded UPDATE by primary key. Holding the order lock above now makes this
                    // unreachable — confirming a reservation requires that lock — so it stands as
                    // the second of two independent guards rather than as the only one.
                    var retired = await db.Database.ExecuteSqlAsync(
                        $"""
                         UPDATE stock_reservations
                         SET status = {(int)ReservationStatus.Released}
                         WHERE id = {reservation.Id}
                           AND status = {held}
                         """,
                        token);

                    if (retired != 1)
                    {
                        logger.LogInformation(
                            "Reservation {ReservationId} was confirmed while this sweep held it. "
                            + "Leaving its {Quantity} unit(s) on the ledger.",
                            reservation.Id,
                            reservation.Quantity);

                        continue;
                    }

                    var released = await db.Database.ExecuteSqlAsync(
                        $"""
                         UPDATE stock_items
                         SET reserved = reserved - {reservation.Quantity}
                         WHERE variant_id = {reservation.VariantId}
                           AND deleted_at IS NULL
                           AND reserved >= {reservation.Quantity}
                         """,
                        token);

                    if (released != 1)
                    {
                        logger.LogWarning(
                            "Reservation {ReservationId} claimed {Quantity} of variant {VariantId}, but "
                            + "the ledger did not hold them. It is retired regardless so it stops being swept.",
                            reservation.Id,
                            reservation.Quantity,
                            reservation.VariantId);
                    }
                    else
                    {
                        // Counted only when the ledger actually moved: this method reports units
                        // returned to a shelf, and the branch above returned none.
                        units += reservation.Quantity;
                    }
                }

                // Safe by construction: the row was selected under FOR UPDATE with status = Pending
                // and that lock is still held, so nothing can have moved it. Pending implies nothing
                // captured, which is what keeps Order.Cancel() — which refuses an order still
                // holding funds — from throwing.
                order.Cancel();

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return (units, true);
            },
            cancellationToken);
    }
}
