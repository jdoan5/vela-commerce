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
    TimeProvider timeProvider,
    ILogger<ReservationReaper> logger) : BackgroundService
{
    /// <summary>How often to look. Well inside the reservation window, so a lapse is noticed promptly.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Bounded so one sweep cannot hold a connection open across thousands of rows.</summary>
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>Releases every reservation whose window has closed. Returns how many it reclaimed.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();

        var now = timeProvider.GetUtcNow();

        var lapsed = await db.StockReservations
            .Where(reservation => reservation.Status == ReservationStatus.Held && reservation.ExpiresAt <= now)
            .OrderBy(reservation => reservation.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (lapsed.Count == 0)
            return 0;

        var reclaimed = 0;

        foreach (var reservation in lapsed)
        {
            // Mirrors the checkout's reservation statement exactly, guarded so it cannot
            // underflow. A row count of zero means the ledger no longer holds what this
            // reservation claims — worth a warning, not worth failing the sweep.
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
                    "Reservation {ReservationId} claimed {Quantity} of variant {VariantId}, but the "
                    + "ledger did not hold them. Marking it released anyway so it stops being swept.",
                    reservation.Id,
                    reservation.Quantity,
                    reservation.VariantId);
            }

            reservation.Release();
            reclaimed += reservation.Quantity;
        }

        // Cancel the orders those reservations were holding stock for. An order still Pending
        // when its window closes was never paid, and leaving it Pending would leave the shopper
        // looking at a purchase that is never going to happen.
        var orderIds = lapsed.Select(reservation => reservation.OrderId).Distinct().ToList();

        var abandoned = await db.Orders
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Where(order => orderIds.Contains(order.Id) && order.Status == OrderStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var order in abandoned)
        {
            order.Cancel();
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Reclaimed {Units} unit(s) from {Reservations} lapsed reservation(s) and cancelled {Orders} order(s).",
            reclaimed,
            lapsed.Count,
            abandoned.Count);

        return reclaimed;
    }
}
