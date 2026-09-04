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
    /// Releases every reservation whose window has closed. Returns how many units it reclaimed.
    /// <para>
    /// The whole sweep is ONE transaction and every row is claimed under a lock, because this
    /// worker races the settlement receiver over the same rows. Without that, an adversarial
    /// review reproduced two money-losing interleavings every run: the reaper reading an order
    /// as Pending, a settlement paying it, and the reaper's blind write turning a captured
    /// payment into a Cancelled order; and the mirror, where a settlement paid the order while
    /// the reaper released its reservations, so the timeline later shipped it having moved zero
    /// units and the stock went back on sale already sold.
    /// </para>
    /// <para>
    /// Reads use bare <c>IgnoreQueryFilters()</c>: the tenancy filter fails closed and would
    /// blind a worker with no visitor, and the surviving soft-delete filter alone is enough to
    /// make EF wrap the statement and bury the locking clause.
    /// </para>
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // EVERY ATTEMPT STARTS FROM A CLEARED CHANGE TRACKER, AND THE SCOPE ABOVE IS WHY.
            //
            // The context is resolved once, outside this lambda, so a retry reuses it. The strategy
            // re-runs the WHOLE body on a transient fault, and without this line the previous
            // attempt's mutations are still tracked as Modified: the orders it called Cancel() on
            // are still pending a flush, against rows this attempt has not re-claimed and holds no
            // lock on. The next SaveChanges would write them anyway — a cancellation applied from a
            // stale read, which is the interleaving the locks below exist to prevent, arriving by
            // the back door.
            //
            // Every other execution-strategy transaction in this solution clears here for the same
            // reason. This one did not, which was an omission rather than a decision.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var now = timeProvider.GetUtcNow();
            var held = (int)ReservationStatus.Held;
            var pending = (int)OrderStatus.Pending;

            // ORDER ROWS ARE LOCKED FIRST, AND THAT ORDERING IS THE WHOLE POINT OF THIS SHAPE.
            //
            // This worker used to take its locks the other way round — reservations, then the
            // orders they belonged to. Every other writer of these two tables goes orders-first:
            // the settlement receiver locks the order row before confirming reservations, and the
            // timeline worker and the refund handler both do the same. Two writers taking the same
            // two rows in opposite orders is a deadlock, and it was a reachable one, reproduced
            // against the real receiver as PostgreSQL 40P01 in exactly the situation both pieces of
            // code exist for: a settlement landing on an order whose reservations have just lapsed.
            //
            // Nothing was corrupted by it — PostgreSQL detects the cycle and aborts one side — but
            // the side it aborted was a settlement, which surfaced as a 500 to the payment gateway
            // from a receiver whose entire design is built never to do that.
            //
            // The step that makes the reordering possible is the unlocked read below. The reaper
            // cannot know which orders to lock until it has looked at reservations, so it looks
            // WITHOUT taking a lock, then locks the orders, then re-reads their reservations under
            // that lock. Everything the unlocked read saw is re-checked afterwards, so a row that
            // changed in between costs this sweep nothing but a wasted candidate.
            // The join filters to PENDING orders, and without it this query starves. A Paid order
            // whose settlement failed to confirm its reservations leaves them Held forever —
            // correctly, since somebody bought those units — but they still match the reservation
            // predicates, so such an order would be returned as a candidate by every sweep for the
            // rest of the deployment's life and rejected by the locking step every time. Order ids
            // are UUIDv7 and therefore sort by age, so once BatchSize of them exist the oldest
            // permanently fill the batch and no newer abandoned checkout is ever reached again.
            //
            // Reading orders unlocked here takes no lock and so cannot participate in a cycle; the
            // status is checked again under FOR UPDATE below, which is the read that decides.
            var candidates = await db.Database
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

            if (candidates.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return 0;
            }

            // SKIP LOCKED, so an order somebody else is holding is left for the next sweep rather
            // than queued behind them. Whoever holds the row owns the decision about it: a
            // settlement in flight will take the order out of Pending, and if it does not, this
            // order is still here in a minute. ORDER BY id so two replicas sweeping at once take
            // multiple orders in the same sequence and cannot deadlock against each other either.
            var orders = await db.Orders
                .FromSql(
                    $"""
                     SELECT *
                     FROM orders
                     WHERE id = ANY({candidates.ToArray()})
                       AND status = {pending}
                       AND deleted_at IS NULL
                     ORDER BY id
                     FOR UPDATE SKIP LOCKED
                     """)
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);

            if (orders.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return 0;
            }

            var reclaimed = 0;
            var swept = 0;

            foreach (var order in orders)
            {
                // EVERY Held reservation this order has, not only the lapsed ones. The order is
                // about to be cancelled, so leaving any of its lines promised would strand those
                // units on a shelf nobody can sell from. It also removes a hazard the old shape
                // had: with a batch limit counted in reservations, one order's lines could split
                // across two sweeps and the first could cancel the order while the second still
                // held stock for it.
                var reservations = await db.StockReservations
                    .FromSql(
                        $"""
                         SELECT *
                         FROM stock_reservations
                         WHERE order_id = {order.Id}
                           AND status = {held}
                           AND deleted_at IS NULL
                         ORDER BY id
                         FOR UPDATE
                         """)
                    .IgnoreQueryFilters()
                    .ToListAsync(cancellationToken);

                foreach (var reservation in reservations)
                {
                    // Guarded on status rather than trusting the object read a moment ago:
                    // StockReservation.Release() refuses a Confirmed row, but it judges an
                    // in-memory copy and EF then emits an unguarded UPDATE by primary key. The row
                    // count is the real answer. Holding the order lock above now makes this
                    // unreachable — confirming a reservation requires that lock — so it is kept as
                    // the second of two independent guards rather than as the only one.
                    var claimed = await db.Database.ExecuteSqlAsync(
                        $"""
                         UPDATE stock_reservations
                         SET status = {(int)ReservationStatus.Released}
                         WHERE id = {reservation.Id}
                           AND status = {held}
                         """,
                        cancellationToken);

                    if (claimed != 1)
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
                        cancellationToken);

                    if (released != 1)
                    {
                        logger.LogWarning(
                            "Reservation {ReservationId} claimed {Quantity} of variant {VariantId}, but "
                            + "the ledger did not hold them. It is released regardless so it stops being swept.",
                            reservation.Id,
                            reservation.Quantity,
                            reservation.VariantId);
                    }
                    else
                    {
                        // Counted only when the ledger actually moved. This method's contract is
                        // "how many units it reclaimed", and the branch above is the case where it
                        // reclaimed none — the reservation is still retired so it stops being
                        // swept, but adding its quantity here would report units that never went
                        // back on any shelf, in the log line an operator reads during an incident.
                        reclaimed += reservation.Quantity;
                    }

                    swept++;
                }

                // Safe by construction rather than by luck: the row was selected under
                // FOR UPDATE with status = Pending and that lock is still held, so nothing can
                // have moved it since. Pending implies nothing captured, which is what keeps
                // Order.Cancel() — which refuses an order still holding funds — from throwing.
                order.Cancel();
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Reclaimed {Units} unit(s) from {Reservations} lapsed reservation(s) and cancelled {Orders} order(s).",
                reclaimed,
                swept,
                orders.Count);

            return reclaimed;
        });
    }
}
