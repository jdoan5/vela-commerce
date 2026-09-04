using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The reaper returns stock that a checkout reserved and never paid for, and it had no tests at all.
/// <para>
/// That absence mattered more than it looked. This is the last piece of the system that moves money
/// and stock without a shopper standing in front of it: it runs on a timer, with no request and no
/// session, and it writes to the two tables the whole demo's headline claim rests on. Its own doc
/// comment records that an adversarial review reproduced two money-losing interleavings before the
/// locks went in — a captured payment overwritten to Cancelled, and a paid order whose reservations
/// were released so the timeline later shipped it having moved zero units. Nothing in the suite
/// would have noticed either coming back.
/// </para>
/// <para>
/// <b>Every test here drives <see cref="ReservationReaper.SweepAsync"/> directly rather than
/// starting the BackgroundService.</b> The loop sleeps for a minute between sweeps, so a test that
/// started the service would either wait a minute or assert on a race with its own timer. One sweep,
/// called explicitly, is the same code with the timing removed.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ReservationReaperTests(PostgresFixture fixture)
{
    /// <summary>
    /// The instant every sweep in this file runs at, and it is in 2020 on purpose.
    /// <para>
    /// The whole assembly shares one container, and a sweep is GLOBAL: it claims every lapsed
    /// reservation in the database, not just the ones the calling test made. Every other test file
    /// reserves stock against the real clock, so its reservations expire fifteen minutes from now —
    /// somewhere in 2026. A reaper told the time is 2020 cannot consider any of them lapsed, so the
    /// exact counts asserted below stay exact however the suite is ordered or sharded.
    /// </para>
    /// <para>
    /// The alternative — a clock near the real one — passes today and fails whenever the suite runs
    /// at an unlucky hour, which is the worst way for a test about correctness to be wrong.
    /// </para>
    /// </summary>
    private static readonly DateTimeOffset Now = new(2020, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How many sweeps race the settlement. More than one on purpose — see the comment in the race
    /// test — and small enough that the fixture's connection pool is not the thing under test.
    /// </summary>
    private const int Sweepers = 6;

    private static readonly ShippingAddress Address = new()
    {
        Recipient = "Ada Lovelace",
        Line1 = "12 Marylebone Road",
        City = "London",
        PostalCode = "NW1 5LA",
        CountryCode = "GB"
    };

    /// <summary>
    /// A clock the test moves by hand.
    /// <para>
    /// Hand-rolled rather than pulled from <c>Microsoft.Extensions.TimeProvider.Testing</c>: the
    /// reaper reads <see cref="TimeProvider.GetUtcNow"/> and nothing else on this path, so a package
    /// reference would buy a scheduler nothing here uses.
    /// </para>
    /// </summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Value;
    }

    /// <summary>
    /// Builds a reaper over the test container.
    /// <para>
    /// <b>The retry strategy is configured exactly as the real host configures it</b>, and that is
    /// not decoration. A retrying execution strategy refuses a user-initiated transaction unless the
    /// whole unit is handed to it, and <c>SweepAsync</c> is shaped around that — wrapping its
    /// transaction in <c>CreateExecutionStrategy().ExecuteAsync</c>. A test host without retries
    /// would exercise a code path the deployment does not have.
    /// </para>
    /// </summary>
    private (ReservationReaper Reaper, FixedClock Clock, ServiceProvider Provider) NewReaper(DateTimeOffset? at = null)
    {
        var clock = new FixedClock(at ?? Now);

        var provider = new ServiceCollection()
            .AddDbContext<VelaCommerceDbContext>(options => options.UseNpgsql(
                fixture.ConnectionString,
                npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)))
            .BuildServiceProvider();

        var reaper = new ReservationReaper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            NullLogger<ReservationReaper>.Instance);

        return (reaper, clock, provider);
    }

    /// <summary>One product with a known number of units on the shelf, private to the calling test.</summary>
    private async Task<Guid> StockAsync(VelaCommerceDbContext db, int onHand)
    {
        var slug = $"reaper-{Guid.CreateVersion7():N}";
        var product = new Product(slug, "Storm Jib", "Seeded by a reaper test.", "rope-and-rigging");
        var variant = product.AddVariant($"RPR-{Guid.NewGuid():N}"[..18], "Standard", new Money(4_200));

        db.Products.Add(product);
        db.StockItems.Add(new StockItem(variant.Id, onHand));
        await db.SaveChangesAsync();

        return variant.Id;
    }

    /// <summary>
    /// Places an order the way checkout does: an aggregate built from a cart, a reservation row, and
    /// the stock ledger incremented to match. Built through the domain rather than by INSERT, so a
    /// test cannot assert against a state the application could never have produced.
    /// </summary>
    private async Task<(Order Order, StockReservation Reservation)> ReserveAsync(
        VelaCommerceDbContext db,
        Guid variantId,
        int quantity,
        DateTimeOffset expiresAt,
        Guid? session = null)
    {
        var demoSession = session ?? Guid.CreateVersion7();

        var cart = new Cart(demoSession);
        cart.AddItem(variantId, "RPR-SKU", "Storm Jib", new Money(4_200), quantity);

        var order = Order.FromCart(
            cart,
            $"VELA-{Guid.NewGuid().ToString("N")[..7].ToUpperInvariant()}",
            $"reaper-{Guid.CreateVersion7():N}",
            Address,
            Money.Zero(),
            Money.Zero(),
            Now.AddMinutes(-20));

        var reservation = new StockReservation(variantId, order.Id, quantity, expiresAt);

        db.Orders.Add(order);
        db.StockReservations.Add(reservation);

        var reserved = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE stock_items
             SET reserved = reserved + {quantity}
             WHERE variant_id = {variantId}
               AND deleted_at IS NULL
               AND on_hand - reserved >= {quantity}
             """);

        Assert.Equal(1, reserved);

        await db.SaveChangesAsync();

        return (order, reservation);
    }

    /// <summary>The two numbers the whole stock argument is about, read with no session.</summary>
    private async Task<(int OnHand, int Reserved)> LedgerAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();

        var item = await db.StockItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(entity => entity.VariantId == variantId);

        return (item.OnHand, item.Reserved);
    }

    private async Task<(OrderStatus Status, ReservationStatus Reservation)> StateAsync(
        Guid orderId,
        Guid reservationId)
    {
        await using var db = fixture.CreateContext();

        var order = await db.Orders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(entity => entity.Id == orderId);

        var reservation = await db.StockReservations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(entity => entity.Id == reservationId);

        return (order.Status, reservation.Status);
    }

    /// <summary>
    /// Confirms the reaper reads rows a request-scoped caller could not see.
    /// <para>
    /// The order claim runs with query filters suppressed, and it has to: DemoTenancy is written as
    /// <c>CurrentDemoSessionId != null &amp;&amp; ...</c> so it FAILS CLOSED, and this worker has no
    /// visitor at all. A filtered query here would match nothing, the sweep would cancel no orders,
    /// and it would look exactly like a shop with nothing to reap — the same green as working. This
    /// test owns the order to a session nobody is holding, so a regression to a filtered read shows
    /// up as an order left Pending rather than as silence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_worker_with_no_visitor_still_reaps_orders_that_belong_to_one()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 3);

        var stranger = Guid.CreateVersion7();
        var (order, _) = await ReserveAsync(db, variantId, 1, Now.AddMinutes(-1), session: stranger);

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        Assert.Equal(1, await reaper.SweepAsync(CancellationToken.None));

        await using var fresh = fixture.CreateContext();

        // Read back through the filter the reaper suppressed, to show the row really is one a
        // session-less reader cannot see by default.
        Assert.Empty(await fresh.Orders.Where(entity => entity.Id == order.Id).ToListAsync());

        var cancelled = await fresh.Orders
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == order.Id);

        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task A_reservation_still_inside_its_window_is_left_alone()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 4);

        // The shopper is still on the payment page. Releasing here would take the unit out from
        // under somebody who is mid-checkout.
        var (order, reservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(10));

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        Assert.Equal(0, await reaper.SweepAsync(CancellationToken.None));
        Assert.Equal((4, 2), await LedgerAsync(variantId));

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);
        Assert.Equal(OrderStatus.Pending, status);
        Assert.Equal(ReservationStatus.Held, reservationStatus);
    }

    /// <summary>
    /// A confirmed reservation belongs to an order somebody paid for. Its units are sold, not
    /// promised, and the fact that its expiry has passed means nothing — the window is a deadline
    /// for paying, and payment already happened.
    /// </summary>
    [Fact]
    public async Task A_confirmed_reservation_is_never_swept_however_long_ago_it_expired()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 6);

        var (order, reservation) = await ReserveAsync(db, variantId, 3, Now.AddHours(-3));

        order.MarkPaid(order.Total, "pay_reaper_fixture", Now.AddHours(-3));
        reservation.Confirm();
        await db.SaveChangesAsync();

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        Assert.Equal(0, await reaper.SweepAsync(CancellationToken.None));

        // The units stay promised. on_hand only falls when the parcel ships.
        Assert.Equal((6, 3), await LedgerAsync(variantId));

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);
        Assert.Equal(OrderStatus.Paid, status);
        Assert.Equal(ReservationStatus.Confirmed, reservationStatus);
    }

    /// <summary>
    /// THE MONEY-LOSING INTERLEAVING THE ORDER CLAIM'S STATUS PREDICATE EXISTS TO STOP.
    /// <para>
    /// The reaper's own doc records it: a settlement pays an order while a sweep holds its
    /// reservations, and a blind write turns a captured payment into a Cancelled order. The
    /// surviving state — Paid, with a reservation the settlement never got round to confirming — is
    /// exactly what a sweep meets afterwards, and it is reproducible without a race because the
    /// damage is done by the write, not by the timing.
    /// </para>
    /// <para>
    /// The units genuinely do go back, and that is correct: a Held reservation past its window is
    /// not holding them for anyone. What must not happen is the order being cancelled with money on
    /// it.
    /// </para>
    /// <para>
    /// <b>Two independent guards stand here, and deleting the SQL one was tried to find out which.</b>
    /// Removing <c>AND status = {pending}</c> from the order claim does not silently destroy the
    /// capture — it reaches <c>Order.Cancel()</c>, which refuses an order still holding captured
    /// funds, and the whole sweep transaction rolls back. So the SQL predicate is the one that keeps
    /// the sweep working, and the aggregate is the one that keeps it honest. Before refunds added
    /// that second guard, the same edit would have turned a paid order into a cancelled one with no
    /// error anywhere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_paid_order_is_never_cancelled_by_a_sweep_however_its_reservations_ended_up()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 5);

        var (order, reservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(-30));

        // Paid, but the reservation was left Held — the settlement path's documented oversell bug.
        order.MarkPaid(order.Total, "pay_reaper_fixture", Now.AddMinutes(-25));
        await db.SaveChangesAsync();

        var captured = order.Captured;

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        await reaper.SweepAsync(CancellationToken.None);

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);

        Assert.Equal(OrderStatus.Paid, status);
        Assert.Equal(ReservationStatus.Released, reservationStatus);

        await using var fresh = fixture.CreateContext();
        var after = await fresh.Orders.IgnoreQueryFilters().SingleAsync(entity => entity.Id == order.Id);

        Assert.Equal(captured, after.Captured);
        Assert.True(after.Refunded.IsZero);
    }

    /// <summary>
    /// Sweeping twice must not release the same units twice. The claim is guarded on
    /// <c>status = Held</c> and the ledger write on <c>reserved &gt;= q</c>, so a duplicate finds
    /// nothing to do rather than driving the counter negative and tripping
    /// <c>ck_stock_items_reserved_non_negative</c>.
    /// </summary>
    [Fact]
    public async Task Sweeping_twice_reclaims_the_units_once()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 8);

        await ReserveAsync(db, variantId, 5, Now.AddMinutes(-2));

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        Assert.Equal(5, await reaper.SweepAsync(CancellationToken.None));
        Assert.Equal(0, await reaper.SweepAsync(CancellationToken.None));

        Assert.Equal((8, 0), await LedgerAsync(variantId));
    }

    /// <summary>
    /// One sweep, several abandoned checkouts, one product. The per-reservation loop has to add up:
    /// a sweep that released three reservations but only decremented the ledger once would leave the
    /// product permanently short and nothing would say so.
    /// </summary>
    [Fact]
    public async Task One_sweep_reclaims_every_lapsed_reservation_on_the_same_product()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 10);

        await ReserveAsync(db, variantId, 2, Now.AddMinutes(-9));
        await ReserveAsync(db, variantId, 3, Now.AddMinutes(-8));
        await ReserveAsync(db, variantId, 1, Now.AddMinutes(-7));

        // And one that has not lapsed, to prove the sweep is selective rather than total.
        var (live, liveReservation) = await ReserveAsync(db, variantId, 4, Now.AddMinutes(30));

        Assert.Equal((10, 10), await LedgerAsync(variantId));

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        Assert.Equal(6, await reaper.SweepAsync(CancellationToken.None));
        Assert.Equal((10, 4), await LedgerAsync(variantId));

        var (status, reservationStatus) = await StateAsync(live.Id, liveReservation.Id);
        Assert.Equal(OrderStatus.Pending, status);
        Assert.Equal(ReservationStatus.Held, reservationStatus);
    }

    /// <summary>
    /// THE INTERLEAVING THAT USED TO LOSE MONEY, DRIVEN DETERMINISTICALLY.
    /// <para>
    /// The reaper's doc records the failure: a settlement confirms a reservation while a sweep is
    /// releasing it, and the sweep's write turns a sold unit back into a shelved one — a paid order
    /// whose stock went back into the pool, which the timeline later ships having moved nothing.
    /// Reproducing that by launching both writers at once does not work; it was tried, and the two
    /// never actually met, because a sweep is a short database transaction and a settlement is an
    /// HTTP round trip, so one reliably finishes before the other begins.
    /// </para>
    /// <para>
    /// So the test takes the settlement's lock itself. It opens a transaction, selects the
    /// reservation <c>FOR UPDATE</c> — which is exactly what the settlement path holds while it
    /// confirms — and only then starts a sweep. The sweep now has no choice but to meet a row
    /// somebody else is committing to, which is the whole point, and the ordering is decided by the
    /// test rather than by which task the scheduler happened to run first.
    /// </para>
    /// <para>
    /// <b>Two independent guards make this safe, and neither can be caught alone.</b> The claim's
    /// <c>FOR UPDATE SKIP LOCKED</c> means a locked row is passed over entirely. If that is removed,
    /// the guarded write — <c>AND status = held</c> — blocks on the same lock, wakes up after the
    /// confirm has committed, matches zero rows and leaves the units alone. Deleting either one on
    /// its own changes nothing observable, which is why single-mutant runs of this file stay green;
    /// deleting BOTH produces the Paid-and-Released state the assertion below refuses. That was
    /// verified by doing it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_sweep_cannot_release_a_reservation_another_transaction_is_confirming()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 4);

        var (order, reservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(-5));

        // The settlement's grip on the row, taken before the sweep exists so the ordering is a fact
        // rather than a hope.
        await using var settlement = fixture.CreateContext();
        await using var holding = await settlement.Database.BeginTransactionAsync();

        var claimed = await settlement.StockReservations
            .FromSql($"SELECT * FROM stock_reservations WHERE id = {reservation.Id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync();

        Assert.Single(claimed);

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        // Started, not awaited: with the lock removed from the claim the sweep BLOCKS on the row
        // above, so awaiting it here would deadlock the test against its own transaction.
        var sweep = reaper.SweepAsync(CancellationToken.None);

        // The settlement finishes what it started.
        claimed[0].Confirm();

        var paid = await settlement.Orders
            .FromSql($"SELECT * FROM orders WHERE id = {order.Id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync();

        paid[0].MarkPaid(paid[0].Total, "pay_race_fixture", Now);

        await settlement.SaveChangesAsync();
        await holding.CommitAsync();

        await sweep;

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);

        Assert.Equal(OrderStatus.Paid, status);

        Assert.Equal(ReservationStatus.Confirmed, reservationStatus);

        // The units are SOLD. A sweep that released them would put stock back on a shelf it had
        // already left, and the next shopper would buy a unit that is spoken for.
        Assert.Equal((4, 2), await LedgerAsync(variantId));
    }

    /// <summary>
    /// The reaper is only worth anything if the host actually starts it.
    /// <para>
    /// Every other test in this file drives <c>SweepAsync</c> directly, which is the right way to
    /// test the logic and the wrong way to notice that nobody runs it. The cart endpoints once
    /// shipped unmapped for exactly this shape of reason — a slice fully built, fully tested, and
    /// never composed — and a reaper that is registered nowhere fails silently and permanently:
    /// abandoned checkouts simply hold their units for good, and the shop slowly stops selling
    /// things with no error anywhere.
    /// </para>
    /// <para>
    /// <c>AddHostedService&lt;T&gt;</c> registers under <see cref="IHostedService"/> rather than
    /// under the concrete type, so the assertion has to look through the descriptors for the
    /// implementation type rather than resolving it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_host_registers_the_reaper_so_something_actually_sweeps()
    {
        var services = new ServiceCollection();
        services.AddCheckout();

        var hosted = services
            .Where(service => service.ServiceType == typeof(IHostedService))
            .Select(service => service.ImplementationType)
            .ToList();

        Assert.Contains(typeof(ReservationReaper), hosted);
    }

    /// <summary>
    /// A reservation whose units the ledger no longer holds is still retired, but is not counted as
    /// reclaimed.
    /// <para>
    /// The two tables can disagree — a reservation row saying units are promised while
    /// <c>stock_items.reserved</c> has already been decremented by something else. The guarded write
    /// refuses to decrement below what is there, which is what stops
    /// <c>ck_stock_items_reserved_non_negative</c> being tripped by a double release. The
    /// reservation is retired anyway, deliberately, so it stops being swept every minute forever.
    /// </para>
    /// <para>
    /// The return value is the part worth pinning. It is documented as how many units the sweep
    /// reclaimed, and it used to be incremented on this path too — so a sweep that put nothing back
    /// on any shelf reported that it had, in the log line an operator reads while working out what
    /// happened. Found by writing this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_reservation_the_ledger_no_longer_backs_is_retired_without_being_counted()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 5);

        var (order, reservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(-5));

        // Something else already gave the units back — a partially-applied release, a hand-written
        // fix during an incident. The reservation row still says they are promised.
        var corrected = await db.Database.ExecuteSqlAsync(
            $"UPDATE stock_items SET reserved = 0 WHERE variant_id = {variantId}");

        Assert.Equal(1, corrected);
        Assert.Equal((5, 0), await LedgerAsync(variantId));

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        // Nothing was reclaimed, because there was nothing left to reclaim.
        Assert.Equal(0, await reaper.SweepAsync(CancellationToken.None));

        // And the ledger is untouched rather than driven to -2, which the CHECK constraint would
        // have refused and which would have failed the whole sweep for every other order in it.
        Assert.Equal((5, 0), await LedgerAsync(variantId));

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);
        Assert.Equal(ReservationStatus.Released, reservationStatus);
        Assert.Equal(OrderStatus.Cancelled, status);
    }

    /// <summary>
    /// The composed path: a real settlement and several real sweeps let go at once.
    /// <para>
    /// <b>What this does NOT prove, said plainly.</b> It is not the test that catches the
    /// interleaving — that is
    /// <see cref="A_sweep_cannot_release_a_reservation_another_transaction_is_confirming"/>, which
    /// controls the ordering instead of hoping for it. Deleting the claim's lock, or the write's
    /// status guard, leaves this test green: measured, over eight runs each. A sweep is a short
    /// database transaction and a settlement is an HTTP round trip through the receiver, so on this
    /// hardware one reliably finishes before the other starts, and no number of concurrent sweepers
    /// changed that.
    /// </para>
    /// <para>
    /// What it is worth keeping for is the shape nothing else covers: several sweeps running at once
    /// against one shelf, sharing work through <c>FOR UPDATE SKIP LOCKED</c> the way two replicas
    /// would, with a genuine signed settlement landing among them — and the assertion that whatever
    /// order they land in, the result is one of two consistent states and never a mixture.
    /// </para>
    /// <para>
    /// <b>There are two acceptable answers and this test accepts either.</b> The settlement wins and
    /// the order is Paid with its reservation Confirmed and its units still promised; or the sweep
    /// wins and the order is Cancelled with its reservation Released and its units back on the
    /// shelf. What must never appear is a mixture — a Paid order whose units went back into the pool
    /// (sold twice), or a Cancelled order holding a capture (money stranded). Asserting a single
    /// expected outcome would be asserting who wins a race, which is not a property the system has.
    /// </para>
    /// <para>
    /// The reservation is backdated with an UPDATE, and that is the one manufactured fact here. It
    /// stands in for time passing: a real reservation reaches this state by sitting unpaid for
    /// fifteen minutes, and no test is going to wait. Everything else — the cart, the checkout, the
    /// stock increment, the signed payload — was produced by the application.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settlement_landing_during_a_sweep_leaves_the_order_consistent_either_way()
    {
        using var lab = new SettlementLab(fixture);

        var jib = await lab.StockAsync("Storm jib", onHand: 3);

        // Delay, so the order is left Pending with a signed notification waiting in the outbox —
        // which is exactly the window in which a reaper and a settlement can collide.
        var placed = await lab.CheckoutAsync(jib, scenario: "Delay");

        var pending = await lab.ReservationsForAsync(jib);
        Assert.Equal("Held", Assert.Single(pending).Status);

        var notification = (await lab.OutboxForAsync(placed.OrderNumber))[0];

        // Time passes. The reservation's window closes while the settlement is still in flight.
        await using (var db = fixture.CreateContext())
        {
            var backdated = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE stock_reservations
                 SET expires_at = {Now.AddMinutes(-5)}
                 WHERE order_id = (SELECT id FROM orders WHERE order_number = {placed.OrderNumber})
                 """);

            Assert.Equal(1, backdated);
        }

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        // Six sweepers rather than one, because concurrent sweeping is the deployment's own shape:
        // the claim is FOR UPDATE SKIP LOCKED precisely so two replicas share the work instead of
        // queueing behind each other, and nothing else in the suite runs two sweeps at once. It was
        // also an attempt to widen the window enough to catch the interleaving here, which did not
        // work - see the note above.
        var outcomes = await Storefront.AllAtOnceAsync(
            Sweepers + 1,
            index => index == 0
                ? lab.DeliverAsync(notification).ContinueWith(t => (object)t.Result, TaskScheduler.Default)
                : reaper.SweepAsync(CancellationToken.None).ContinueWith(t => (object)t.Result, TaskScheduler.Default));

        Assert.Equal(Sweepers + 1, outcomes.Length);

        var order = await lab.OrderAsync(placed.OrderNumber);
        var reservation = Assert.Single(await lab.ReservationsForAsync(jib));
        var ledger = await lab.LedgerAsync(jib);

        // One consistent pair, never a mixture. Named explicitly so a failure prints which
        // impossible state was reached rather than which equality did not hold.
        var settlementWon = order.Status == nameof(OrderStatus.Paid)
                            && reservation.Status == nameof(ReservationStatus.Confirmed)
                            && ledger.Reserved == 1;

        var sweepWon = order.Status == nameof(OrderStatus.Cancelled)
                       && reservation.Status == nameof(ReservationStatus.Released)
                       && ledger.Reserved == 0
                       && order.CapturedAmount == 0;

        Assert.True(
            settlementWon || sweepWon,
            $"The settlement and the sweep left the order in a state neither of them should produce: "
            + $"order {order.Status}, capture {order.CapturedAmount}, reservation {reservation.Status}, "
            + $"ledger on_hand {ledger.OnHand} reserved {ledger.Reserved}. The two consistent answers are "
            + "Paid/Confirmed/reserved=1 or Cancelled/Released/reserved=0 with nothing captured; anything "
            + "else means the units were sold and shelved at once, or a capture was stranded on a "
            + "cancelled order.");

        // Whoever won, on_hand never moves: it only falls when a parcel ships.
        Assert.Equal(3, ledger.OnHand);
    }

    [Fact]
    public async Task A_lapsed_reservation_puts_its_units_back_and_cancels_the_order_that_abandoned_them()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 5);

        // Expired five minutes ago: the shopper reached the payment step and walked away.
        var (order, reservation) = await ReserveAsync(db, variantId, quantity: 2, expiresAt: Now.AddMinutes(-5));

        Assert.Equal((5, 2), await LedgerAsync(variantId));

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        var reclaimed = await reaper.SweepAsync(CancellationToken.None);

        Assert.Equal(2, reclaimed);
        Assert.Equal((5, 0), await LedgerAsync(variantId));

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);
        Assert.Equal(OrderStatus.Cancelled, status);
        Assert.Equal(ReservationStatus.Released, reservationStatus);
    }
}
