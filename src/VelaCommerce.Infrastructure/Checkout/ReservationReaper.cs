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

    /// <summary>Bounded so one sweep cannot hold a connection open across thousands of rows.</summary>
    private const int BatchSize = 100;

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

            // SKIP LOCKED so two replicas share the work instead of queueing behind each other.
            var lapsed = await db.StockReservations
                .FromSql(
                    $"""
                     SELECT *
                     FROM stock_reservations
                     WHERE status = {held}
                       AND expires_at <= {now}
                       AND deleted_at IS NULL
                     ORDER BY expires_at
                     LIMIT {BatchSize}
                     FOR UPDATE SKIP LOCKED
                     """)
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);

            if (lapsed.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return 0;
            }

            var reclaimed = 0;
            var releasedIds = new List<Guid>(lapsed.Count);

            foreach (var reservation in lapsed)
            {
                // Guarded on status, not just on the in-memory copy. StockReservation.Release()
                // refuses a Confirmed row, but it judges the object loaded a moment ago and EF
                // then emits an unguarded UPDATE by primary key. Under the race above that
                // overwrote a reservation the settlement had just confirmed — the domain guard
                // read as protection and provided none. The row count is the real answer.
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
                    // Somebody confirmed it between the claim and here. Its units are genuinely
                    // sold, so the ledger must not be touched.
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
                    // Counted only when the ledger actually moved. This method's contract is "how
                    // many units it reclaimed", and the branch above is the case where it reclaimed
                    // none — the reservation is still marked Released so it stops being swept, but
                    // adding its quantity here would report units that never went back on any shelf,
                    // in the log line an operator reads while working out what a sweep did.
                    reclaimed += reservation.Quantity;
                }

                releasedIds.Add(reservation.OrderId);
            }

            var cancelled = 0;

            foreach (var orderId in releasedIds.Distinct())
            {
                // FOR UPDATE without SKIP LOCKED: this one must wait rather than skip, so a
                // settlement in flight either commits first and takes the order out of Pending,
                // or waits behind this transaction and finds it Cancelled. Either way the two
                // never both believe they won.
                var pending = (int)OrderStatus.Pending;

                var claimedOrders = await db.Orders
                    .FromSql(
                        $"""
                         SELECT *
                         FROM orders
                         WHERE id = {orderId}
                           AND status = {pending}
                           AND deleted_at IS NULL
                         FOR UPDATE
                         """)
                    .IgnoreQueryFilters()
                    .ToListAsync(cancellationToken);

                if (claimedOrders.Count == 0)
                {
                    continue;
                }

                claimedOrders[0].Cancel();
                cancelled++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Reclaimed {Units} unit(s) from {Reservations} lapsed reservation(s) and cancelled {Orders} order(s).",
                reclaimed,
                lapsed.Count,
                cancelled);

            return reclaimed;
        });
    }
}
