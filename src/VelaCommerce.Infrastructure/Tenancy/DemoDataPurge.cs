using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Messaging;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Infrastructure.Tenancy;

/// <summary>
/// Deletes demo data that nobody is coming back for, so a shop strangers share does not grow
/// forever and does not slowly take itself off sale.
/// <para>
/// <b>Two problems, and only one of them is about disk.</b> The obvious one is growth: every
/// visitor mints a session, and carts, orders, price overrides and outbox rows accumulate against a
/// Neon Free project with a half-gigabyte cap and no expiry. The one that actually degrades the
/// demo is <c>stock_items</c>. That ledger is GLOBAL — one row per variant, no session id, shared
/// by every visitor — so a checkout abandoned in a state the reservation reaper is designed not to
/// touch holds its units against everybody, permanently. Enough of those and the stocked-at-1
/// product that the whole race demonstration is built on is sold out for good, with nothing in a
/// log to say why.
/// </para>
/// <para>
/// <b>This is not the nightly job the plan described, and the difference is deliberate.</b>
/// <c>docs/PLAN.md</c> §6 specifies an Azure Container Apps Job on a cron. This is an in-process
/// worker that sweeps while the container is already awake, and
/// <c>docs/adr/0010-the-purge-runs-on-visits-not-on-a-clock.md</c> is the argument: the data only
/// exists because somebody visited, a visit is also the only thing that wakes this container, and
/// the ACA-Job version needs a second Terraform resource, a second hand-pasted connection string
/// and an image nothing in the pipeline can update — the same four moving parts
/// <c>.github/workflows/migrate.yml</c> already weighed and declined. The cost of the choice is
/// stated there too, plainly: a demo nobody visits is never swept.
/// </para>
/// <para>
/// <b>Order of operations, and none of it is arbitrary.</b> Stock is handed back BEFORE anything is
/// deleted, because the reservation rows are the only record of how much to hand back — and
/// <c>stock_reservations</c> has no foreign key to <c>orders</c>, so deleting an order first does
/// not cascade to them, it orphans them. An orphaned Held reservation is unreachable by every
/// existing sweep in this solution: <c>ReservationReaper</c> finds candidates by joining
/// <c>orders</c>, and the visitor's own reset scopes to order ids the tenancy filter returned.
/// Nothing would ever find those units again.
/// </para>
/// <para>
/// Reads run with bare <c>IgnoreQueryFilters()</c> for the two reasons both sibling workers
/// document: the tenancy filter fails closed, so a worker with no visitor would see nothing and
/// silently do nothing; and the surviving soft-delete filter alone is enough to make EF wrap the
/// statement in a subquery and bury the locking clause, which is how a claim quietly stops being a
/// claim.
/// </para>
/// </summary>
public sealed class DemoDataPurge(
    IServiceScopeFactory scopeFactory,
    DemoDataPurgeOptions options,
    TimeProvider timeProvider,
    ILogger<DemoDataPurge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Checked here rather than inside SweepAsync, so a caller holding this object can still
        // drive one sweep deliberately — which is what the integration tests do. Off means "nothing
        // sweeps on a timer", not "sweeping is forbidden".
        if (!options.Enabled)
        {
            logger.LogInformation(
                "The demo data purge is disabled by configuration ({Key}). Nothing will expire.",
                $"{DemoDataPurgeOptions.SectionName}:{nameof(DemoDataPurgeOptions.Enabled)}");

            return;
        }

        // THE DELAY COMES FIRST, WHICH IS THE OPPOSITE OF ReservationReaper AND IS THE POINT.
        //
        // The reaper sweeps on boot because a container that restarted mid-checkout is exactly the
        // case that strands units, and a minute of delay there is a minute of stock off sale. This
        // worker has no such urgency — its rows have already been sitting for a day — and it has a
        // cost the reaper does not: this process scales to zero, so every boot has a visitor
        // waiting on the other end of it. Deleting rows on a quarter of a vCPU while somebody pays
        // a measured 32-second cold start would be spending the scarcest resource this deployment
        // has on the least urgent work it does.
        try
        {
            await Task.Delay(options.FirstSweepDelay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

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
                // Never let one bad sweep kill the service. Failing loudly and trying again beats a
                // worker that dies silently and leaves the growth nobody is watching.
                logger.LogError(exception, "A demo data purge failed. Retrying at the next interval.");
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
    /// Removes everything older than the retention window, in four passes with different rules.
    /// <para>
    /// Orders go one per transaction with the full locking dance, because they touch the shared
    /// ledger. Carts, price overrides and settled outbox rows go in one statement each, because
    /// none of them does.
    /// </para>
    /// </summary>
    public async Task<DemoDataPurgeSummary> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();

        var cutoff = timeProvider.GetUtcNow() - options.Retention;

        var orders = 0;
        var reservations = 0;
        var units = 0;

        foreach (var orderId in await FindExpiredOrdersAsync(db, cutoff, cancellationToken))
        {
            var removed = await PurgeOrderAsync(db, orderId, cutoff, cancellationToken);

            if (!removed.Removed)
            {
                continue;
            }

            orders++;
            reservations += removed.Reservations;
            units += removed.Units;
        }

        var carts = await PurgeCartsAsync(db, cutoff, cancellationToken);
        var overrides = await PurgePriceOverridesAsync(db, cutoff, cancellationToken);
        var outbox = await PurgeSettledOutboxAsync(db, cutoff, cancellationToken);

        var summary = new DemoDataPurgeSummary(orders, reservations, units, carts, overrides, outbox);

        if (summary.IsEmpty)
        {
            // The ordinary case on a demo nobody is hammering, and it must not be Information —
            // a line every six hours saying "nothing happened" is how a log stops being read.
            logger.LogDebug("Demo data purge found nothing older than {Cutoff:o}.", cutoff);
        }
        else
        {
            logger.LogInformation(
                "Purged demo data older than {Cutoff:o}: {Orders} order(s) returning {Units} unit(s) "
                + "from {Reservations} reservation(s), {Carts} cart(s), {Overrides} price override(s), "
                + "{Outbox} settled outbox message(s).",
                cutoff,
                summary.Orders,
                summary.Units,
                summary.Reservations,
                summary.Carts,
                summary.PriceOverrides,
                summary.OutboxMessages);
        }

        return summary;
    }

    /// <summary>
    /// The oldest expired orders, up to a batch. Takes no lock — everything it sees is re-checked
    /// under the lock, so a row that changed in between costs a wasted candidate and nothing else.
    /// <para>
    /// <c>ORDER BY id</c> rather than by <c>placed_at</c>, and they are not the same ordering by
    /// accident: ids are UUIDv7 and sort by the instant the row was minted, so this takes the
    /// genuinely oldest work first and cannot starve. Sorting by <c>placed_at</c> would agree
    /// almost always and disagree exactly when a test backdates one.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> FindExpiredOrdersAsync(
        VelaCommerceDbContext db,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        return await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id AS "Value"
                 FROM orders
                 WHERE placed_at < {cutoff}
                 ORDER BY id
                 LIMIT {options.BatchSize}
                 """)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Hands one expired order's units back and then deletes it, in one transaction.
    /// <para>
    /// <b>Lock order: the order row first, then its reservations by variant, then the ledger.</b>
    /// That is the house convention <c>ReservationReaper</c>, <c>OrderTimelineWorker</c> and the
    /// visitor's own reset all follow, and it is not a preference — the reaper's comment records
    /// that the reservations-first version was reproduced as PostgreSQL <c>40P01</c> against the
    /// real settlement receiver. One transaction per order for the same reason the reaper uses one:
    /// a single transaction across the batch would hold every order row in it while a settlement
    /// for any of them queued behind the whole sweep.
    /// </para>
    /// <para>
    /// <c>SKIP LOCKED</c>, so an order somebody is actively holding is left for the next sweep. It
    /// has waited a day; it can wait six hours more. The alternative is a background job blocking a
    /// visitor's checkout to delete something nobody asked about.
    /// </para>
    /// </summary>
    private async Task<(bool Removed, int Reservations, int Units)> PurgeOrderAsync(
        VelaCommerceDbContext db,
        Guid orderId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async Task<(bool Removed, int Reservations, int Units)> (CancellationToken token) =>
            {
                // Every attempt starts clean, for the reason every other execution-strategy
                // transaction in this solution clears here: a retry reuses the context, and state
                // tracked by the previous attempt is state this attempt holds no lock on.
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                // THE ORDER ROW FIRST, and the age re-checked under the lock rather than trusted
                // from the candidate read. Not defensive dressing: placed_at is settable by the
                // checkout alone today, but "the row still qualifies" is the one claim this
                // transaction is about to act destructively on, and reading it after the lock is
                // what makes it stay true for the length of the transaction.
                var claimed = await db.Orders
                    .FromSql(
                        $"""
                         SELECT *
                         FROM orders
                         WHERE id = {orderId}
                           AND placed_at < {cutoff}
                         FOR UPDATE SKIP LOCKED
                         """)
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .ToListAsync(token);

                if (claimed.Count == 0)
                {
                    await transaction.RollbackAsync(token);
                    return (false, 0, 0);
                }

                var order = claimed[0];
                var reservations = 0;
                var units = 0;

                // Whether the ledger is still holding this order's units is a property of the
                // ORDER's status, not of any reservation's — see OrderStateMachine.HoldingStock,
                // where the argument lives, and note in particular that Shipped is absent because
                // shipping already decremented the ledger and left the reservation Confirmed.
                if (OrderStateMachine.HoldingStock.Contains(order.Status))
                {
                    (reservations, units) = await ReleaseHeldStockAsync(db, orderId, token);
                }

                // Raw DELETE rather than ExecuteDeleteAsync, and the difference is not stylistic:
                // ExecuteDelete applies the entity's query filters, so the surviving soft-delete
                // filter would quietly spare every soft-deleted reservation — leaving exactly the
                // orphan rows this method exists to prevent, and leaving them invisible.
                var reservationRows = await db.Database.ExecuteSqlAsync(
                    $"DELETE FROM stock_reservations WHERE order_id = {orderId}",
                    token);

                // order_lines and refunds go with it: both foreign keys are ON DELETE CASCADE in
                // PostgreSQL, not merely in the change tracker.
                await db.Database.ExecuteSqlAsync(
                    $"DELETE FROM orders WHERE id = {orderId}",
                    token);

                await transaction.CommitAsync(token);

                if (reservationRows != reservations && reservationRows > 0)
                {
                    // Deleted more reservation rows than were released. Ordinary — Released and
                    // soft-deleted rows are deleted and not released — but worth being able to find
                    // if the two ever diverge for a reason that is not ordinary.
                    logger.LogDebug(
                        "Order {OrderId}: released {Released} reservation(s) and deleted {Deleted}.",
                        orderId,
                        reservations,
                        reservationRows);
                }

                return (true, reservations, units);
            },
            cancellationToken);
    }

    /// <summary>
    /// Gives back every unit one expired order is holding, with the same two guards the reset and
    /// the reaper use.
    /// <para>
    /// EVERY reservation that is not already Released, not only the Held ones. A Paid or Packed
    /// order's reservations are <em>Confirmed</em> and the ledger is still holding them —
    /// <c>OrderTimelineWorker</c> only decrements on shipping — so a release that looked at Held
    /// alone would delete a paid order and strand its units. This is where the purge deliberately
    /// parts company with <c>ReservationReaper</c>, which refuses to release a Paid order's
    /// reservations precisely because somebody bought those units. The reaper is right: it leaves
    /// the order alive, so the units are still spoken for. This method is about to delete the
    /// order, and units held for a row that will not exist in a moment are not spoken for, they are
    /// lost. The visitor's own reset made the identical judgement for the identical reason.
    /// </para>
    /// </summary>
    private async Task<(int Reservations, int Units)> ReleaseHeldStockAsync(
        VelaCommerceDbContext db,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var released = (int)ReservationStatus.Released;

        // ORDER BY variant_id joins the convention every other writer of stock_items follows.
        // Reservation id is cart-insertion order and uncorrelated with variant, so sorting by it
        // lets two transactions take two ledger rows in opposite orders and deadlock one table down.
        var claims = await db.StockReservations
            .FromSql(
                $"""
                 SELECT *
                 FROM stock_reservations
                 WHERE order_id = {orderId}
                   AND status <> {released}
                 ORDER BY variant_id, id
                 FOR UPDATE
                 """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var reservations = 0;
        var units = 0;

        foreach (var claim in claims)
        {
            var observed = (int)claim.Status;

            // Guarded on the status it was observed in rather than trusting the row read a moment
            // ago. Only one actor can win this statement, and the loser learns it from a row count
            // of zero rather than from an exception — the same claim-then-act discipline the reaper
            // uses, and what stops two processes both deciding they are the one giving a unit back.
            var retired = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_reservations
                 SET status = {released}
                 WHERE id = {claim.Id}
                   AND status = {observed}
                 """,
                cancellationToken);

            if (retired != 1)
            {
                logger.LogInformation(
                    "Reservation {ReservationId} changed while this purge held it. Leaving its "
                    + "{Quantity} unit(s) on the ledger.",
                    claim.Id,
                    claim.Quantity);

                continue;
            }

            reservations++;

            // The ledger write is StockItem.Release expressed as SQL, for the reason the checkout
            // gives at length: the domain method states the rule correctly but judges an in-memory
            // copy, and only the database can compare and decrement in the same locked instant.
            var decremented = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_items
                 SET reserved = reserved - {claim.Quantity}
                 WHERE variant_id = {claim.VariantId}
                   AND deleted_at IS NULL
                   AND reserved >= {claim.Quantity}
                 """,
                cancellationToken);

            if (decremented != 1)
            {
                logger.LogWarning(
                    "Reservation {ReservationId} claimed {Quantity} of variant {VariantId}, but the "
                    + "ledger did not hold them. It is retired regardless so it stops being swept.",
                    claim.Id,
                    claim.Quantity,
                    claim.VariantId);
            }
            else
            {
                // Counted only when the ledger actually moved: this reports units returned to a
                // shelf, and the branch above returned none.
                units += claim.Quantity;
            }
        }

        return (reservations, units);
    }

    /// <summary>
    /// Deletes expired carts, aged by their own primary key.
    /// <para>
    /// <b>Carts carry no timestamp of any kind</b> — the table is <c>id</c>,
    /// <c>demo_session_id</c>, <c>currency</c>, <c>deleted_at</c> and nothing else — so there is no
    /// column to compare. What there is instead is a guarantee: every id in this schema is a UUIDv7
    /// minted by <c>Guid.CreateVersion7()</c>, and PostgreSQL 18's <c>uuid_extract_timestamp()</c>
    /// recovers the instant it was minted. Verified through Npgsql's binary wire format against
    /// <c>postgres:18-alpine</c>, not assumed: a .NET Guid round-trips byte-identical, PostgreSQL
    /// reports version 7, and the extracted age matches.
    /// </para>
    /// <para>
    /// <b>It fails closed, which is why it is acceptable to age rows by their key at all.</b>
    /// <c>uuid_extract_timestamp</c> returns NULL for any id that is not a v1 or v7 UUID, and
    /// <c>NULL &lt; cutoff</c> is NULL, so a row this function cannot date is a row this statement
    /// does not delete. The failure direction of a bad id is "kept forever", never "deleted early".
    /// </para>
    /// <para>
    /// It is a sequential scan: <c>carts</c> is indexed on <c>demo_session_id</c> only, and an
    /// expression index on the key would need a migration to buy nothing at this size — the table
    /// is bounded by per-session row caps and by this very statement. If it ever stops being small,
    /// the fix is a <c>created_at</c> column, not an index on a workaround.
    /// </para>
    /// <para>
    /// cart_lines go with the cart: <c>fk_cart_lines_carts</c> is ON DELETE CASCADE.
    /// </para>
    /// </summary>
    private static async Task<int> PurgeCartsAsync(
        VelaCommerceDbContext db,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM carts WHERE uuid_extract_timestamp(id) < {cutoff}",
            cancellationToken);

    /// <summary>
    /// Deletes expired price overrides, aged by <c>updated_at</c> rather than <c>created_at</c> — an
    /// overlay a demo admin repriced an hour ago is in use, however long ago it was first written.
    /// </summary>
    private static async Task<int> PurgePriceOverridesAsync(
        VelaCommerceDbContext db,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM demo_catalog_price_overrides WHERE updated_at < {cutoff}",
            cancellationToken);

    /// <summary>
    /// Deletes outbox messages that have reached a terminal state.
    /// <para>
    /// This is the fastest-growing table in the schema — a row per payment event, with nothing
    /// anywhere that ever removed one — and it is the only pass here that is not about a demo
    /// session. It is in this sweep because it is the same problem: a demo that quietly grows
    /// until a free tier stops it.
    /// </para>
    /// <para>
    /// <b>Only Delivered and Abandoned, never Pending.</b> A Pending row is undelivered work; the
    /// dispatcher is still going to try it, and deleting one would drop a notification silently,
    /// which is the exact failure the outbox pattern exists to make impossible. A message stuck
    /// Pending is a bug to find, not a row to sweep — so it stays, and it stays visible.
    /// </para>
    /// </summary>
    private static async Task<int> PurgeSettledOutboxAsync(
        VelaCommerceDbContext db,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var pending = (int)OutboxMessageStatus.Pending;

        return await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM outbox_messages
             WHERE created_at < {cutoff}
               AND status <> {pending}
             """,
            cancellationToken);
    }
}

/// <summary>
/// What one sweep removed. Returned rather than only logged so a test can assert on it, which is
/// the difference between a worker that is tested and one that is watched.
/// </summary>
/// <param name="Orders">Expired orders deleted, with their lines and refunds.</param>
/// <param name="Reservations">Reservations moved to Released before deletion.</param>
/// <param name="Units">Units actually returned to the shared stock ledger.</param>
/// <param name="Carts">Expired carts deleted, with their lines.</param>
/// <param name="PriceOverrides">Expired demo price overlays deleted.</param>
/// <param name="OutboxMessages">Settled outbox messages deleted.</param>
public sealed record DemoDataPurgeSummary(
    int Orders,
    int Reservations,
    int Units,
    int Carts,
    int PriceOverrides,
    int OutboxMessages)
{
    /// <summary>Nothing was old enough to remove.</summary>
    public bool IsEmpty =>
        Orders == 0 && Carts == 0 && PriceOverrides == 0 && OutboxMessages == 0;
}
