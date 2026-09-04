using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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
    /// <summary>
    /// Fails one statement, once, with a fault the provider classes as transient — which is what
    /// makes the retrying execution strategy re-run the whole sweep body. There is no other way to
    /// reach the retry path from outside: transient faults are, by definition, not something a test
    /// can arrange by asking the database nicely.
    /// </summary>
    private sealed class FailOnce(Func<string, bool> match) : DbCommandInterceptor
    {
        private bool _fired;

        public bool Fired => _fired;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>
        /// Both hooks, because EF chooses between them and the choice is not the test's to make: a
        /// bare <c>ExecuteSqlAsync</c> goes through the non-query path, while a batched
        /// <c>SaveChanges</c> update goes through a reader so the provider can hand back affected
        /// row counts. Intercepting only one of them silently matches nothing.
        /// </summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fail(DbCommand command)
        {
            if (_fired || !match(command.CommandText))
            {
                return;
            }

            _fired = true;

            // 40P01 is deadlock_detected. Npgsql classes it transient, so EnableRetryOnFailure runs
            // the whole lambda again rather than surfacing it — which is exactly the second attempt
            // this test is about.
            throw new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01");
        }
    }

    private (ReservationReaper Reaper, FixedClock Clock, ServiceProvider Provider) NewReaper(
        DateTimeOffset? at = null,
        FailOnce? interceptor = null,
        int batchSize = 100)
    {
        var clock = new FixedClock(at ?? Now);

        var provider = new ServiceCollection()
            .AddDbContext<VelaCommerceDbContext>(options =>
            {
                options.UseNpgsql(
                    fixture.ConnectionString,
                    npgsql => npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null));

                if (interceptor is not null)
                {
                    options.AddInterceptors(interceptor);
                }
            })
            .BuildServiceProvider();

        // Enabled is irrelevant here and deliberately left at its default: every test drives
        // SweepAsync directly, and the flag only gates the timer loop in ExecuteAsync.
        var reaper = new ReservationReaper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ReservationReaperOptions { BatchSize = batchSize },
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
    /// A sweep leaves a paid order alone entirely — its status, its capture AND its stock.
    /// <para>
    /// The order's status is the only authority for handing units back, and the reordered sweep
    /// makes that structural: it locks Pending orders first and only then looks at their
    /// reservations, so an order past Pending is never even considered.
    /// </para>
    /// <para>
    /// <b>This is a deliberate change from the old behaviour, and the old behaviour was an
    /// oversell.</b> The sweep used to select reservations on their own — status Held, window
    /// closed — with no predicate anywhere on the owning order, so a Paid order whose settlement
    /// had failed to confirm its reservations had its units released back into the pool fifteen
    /// minutes later while the order stayed Paid. The settlement receiver's own comment names that
    /// exact outcome as "an oversell with no error anywhere": it guards against causing it, and the
    /// reaper then went and did it anyway. Now the units stay promised, which is what they are —
    /// somebody paid for them.
    /// </para>
    /// <para>
    /// The order is never cancelled either, and two independent things stop that. The claim's
    /// <c>AND status = Pending</c> runs first, and <c>Order.Cancel()</c> refuses an order still
    /// holding captured funds — which the refunds work added. Deleting the SQL predicate no longer
    /// destroys a capture; it reaches the aggregate, throws, and rolls the whole sweep back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_sweep_leaves_a_paid_orders_status_capture_and_stock_untouched()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 5);

        var (order, reservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(-30));

        // Paid, but the reservation was left Held — the settlement path's documented oversell bug,
        // and the state that used to make this sweep compound it.
        order.MarkPaid(order.Total, "pay_reaper_fixture", Now.AddMinutes(-25));
        await db.SaveChangesAsync();

        var captured = order.Captured;

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        Assert.Equal(0, await reaper.SweepAsync(CancellationToken.None));

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);

        Assert.Equal(OrderStatus.Paid, status);
        Assert.Equal(ReservationStatus.Held, reservationStatus);

        // The units stay promised rather than going back on a shelf they were sold from.
        Assert.Equal((5, 2), await LedgerAsync(variantId));

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
    /// A settlement holding its order makes a sweep step aside — and, until the lock order was
    /// fixed, deadlocked with it instead.
    /// <para>
    /// The reaper used to take its two locks the other way round: reservations first, then the
    /// orders they belonged to. Every other writer of these tables goes orders-first — the
    /// settlement receiver locks the order row before confirming reservations, and so do the
    /// timeline worker and the refund handler. Two writers taking the same two rows in opposite
    /// orders is a deadlock, and it was reachable in exactly the situation both pieces of code
    /// exist for. Driven against the receiver's real ordering it came back as PostgreSQL
    /// <c>40P01: deadlock detected</c>, aborting the settlement — a 500 to the payment gateway from
    /// a receiver whose whole design is built never to send one.
    /// </para>
    /// <para>
    /// This is the receiver's half of that, in the receiver's own order: take the order row under
    /// <c>FOR UPDATE</c>, then confirm. The sweep now meets the order lock FIRST, skips the row
    /// under <c>SKIP LOCKED</c>, and finishes without touching anything — so there is no cycle to
    /// detect and nothing blocks. Restoring the old ordering makes this fail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settlement_holding_its_order_makes_a_sweep_step_aside_rather_than_deadlock()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 4);

        var (order, reservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(-5));

        await using var settlement = fixture.CreateContext();
        await using var holding = await settlement.Database.BeginTransactionAsync();

        var locked = await settlement.Orders
            .FromSql($"SELECT * FROM orders WHERE id = {order.Id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync();

        Assert.Single(locked);

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        // Awaited with a deadline, because the failure this guards against is a HANG rather than a
        // wrong answer. Under the fixed ordering the sweep cannot block: it meets the order lock
        // first and passes over it. Under the old ordering it claimed the reservation and then
        // queued behind this transaction for the order — which never releases until after this
        // line, so a regression would park the suite forever instead of failing it.
        var sweep = reaper.SweepAsync(CancellationToken.None);

        int reclaimed;

        try
        {
            reclaimed = await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                "The sweep blocked on an order this test holds. That is the old lock ordering back: "
                + "the reaper must take the order row BEFORE the reservations belonging to it, the "
                + "way the settlement receiver, the timeline worker and the refund handler all do. "
                + "Taking them the other way round is the deadlock this test exists to keep fixed.");

            return;
        }

        Assert.Equal(0, reclaimed);

        var confirmed = await settlement.StockReservations
            .FromSql($"SELECT * FROM stock_reservations WHERE id = {reservation.Id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync();

        confirmed[0].Confirm();
        locked[0].MarkPaid(locked[0].Total, "pay_race_fixture", Now);

        await settlement.SaveChangesAsync();
        await holding.CommitAsync();

        var (status, reservationStatus) = await StateAsync(order.Id, reservation.Id);

        Assert.Equal(OrderStatus.Paid, status);
        Assert.Equal(ReservationStatus.Confirmed, reservationStatus);

        // The units are SOLD, and were never handed back to the shelf on the way.
        Assert.Equal((4, 2), await LedgerAsync(variantId));
    }

    /// <summary>
    /// A retried sweep must re-read everything, and must not carry the previous attempt's decisions
    /// into the new transaction.
    /// <para>
    /// The sweep hands its whole transaction to a retrying execution strategy, which is required:
    /// <c>EnableRetryOnFailure</c> refuses a user-initiated transaction unless it can re-run the
    /// entire unit. But the context is resolved once, OUTSIDE that lambda, so a second attempt
    /// reuses it — and every entity the first attempt mutated is still tracked as Modified. An
    /// order it called <c>Cancel()</c> on is still queued for a flush, against a row the new attempt
    /// has not re-claimed and holds no lock on.
    /// </para>
    /// <para>
    /// The reaper was the only execution-strategy transaction in the solution not clearing its
    /// change tracker first; the timeline worker, both checkout transactions and the refund handler
    /// all do, and the timeline's comment says why in as many words. Reverting that one line makes
    /// this test fail with a stale cancellation applied to an order the second attempt left alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_retried_sweep_does_not_apply_the_previous_attempts_decisions()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 6);

        var (first, firstReservation) = await ReserveAsync(db, variantId, 2, Now.AddMinutes(-6));
        var (second, secondReservation) = await ReserveAsync(db, variantId, 1, Now.AddMinutes(-4));

        Assert.Equal((6, 3), await LedgerAsync(variantId));

        // Fail the FINAL WRITE once — the flush that persists the cancellations. Targeting anything
        // earlier proves nothing: the reservations are released by raw SQL and the orders are not
        // loaded until later, so at any earlier point there is no stale tracked state for a retry to
        // carry. It has to be the statement that runs after Cancel() has been called.
        var interceptor = new FailOnce(text => text.Contains("UPDATE orders", StringComparison.Ordinal));

        var (reaper, _, provider) = NewReaper(interceptor: interceptor);
        await using var _ = provider;

        var reclaimed = await reaper.SweepAsync(CancellationToken.None);

        Assert.True(interceptor.Fired, "The interceptor never fired, so no retry happened and this test proved nothing.");

        // The second attempt did the whole job exactly once: both reservations released, both
        // orders cancelled, and the ledger back to where it started.
        Assert.Equal(3, reclaimed);
        Assert.Equal((6, 0), await LedgerAsync(variantId));

        var (firstStatus, firstReservationStatus) = await StateAsync(first.Id, firstReservation.Id);
        var (secondStatus, secondReservationStatus) = await StateAsync(second.Id, secondReservation.Id);

        Assert.Equal(OrderStatus.Cancelled, firstStatus);
        Assert.Equal(OrderStatus.Cancelled, secondStatus);
        Assert.Equal(ReservationStatus.Released, firstReservationStatus);
        Assert.Equal(ReservationStatus.Released, secondReservationStatus);
    }

    /// <summary>
    /// A stuck order must not occupy a place in the batch forever, starving the abandoned checkouts
    /// behind it.
    /// <para>
    /// A Paid order whose settlement failed to confirm its reservations leaves them Held for good —
    /// correctly, because somebody bought those units. But those rows still match every predicate
    /// the reaper uses to notice a lapsed reservation, so before the candidate query joined to
    /// <c>orders</c>, such an order was returned as a candidate by every sweep and rejected by the
    /// locking step every time. Order ids are UUIDv7 and sort by age, so the oldest stuck orders
    /// come first: once a batch's worth of them exists, no newer abandoned checkout is ever reached
    /// again and the shop quietly stops reclaiming stock.
    /// </para>
    /// <para>
    /// Run with a batch of one, which is the whole reason <c>BatchSize</c> is configurable. The
    /// stuck order is created first so its id sorts ahead of the abandoned one; without the join it
    /// takes the only slot and the sweep reclaims nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stuck_order_does_not_crowd_a_genuinely_abandoned_one_out_of_the_batch()
    {
        await using var db = fixture.CreateContext();
        var variantId = await StockAsync(db, onHand: 9);

        // Created FIRST, so its UUIDv7 sorts ahead: paid, but its reservation was never confirmed.
        var (stuck, stuckReservation) = await ReserveAsync(db, variantId, 4, Now.AddHours(-2));
        stuck.MarkPaid(stuck.Total, "pay_stuck_fixture", Now.AddHours(-2));
        await db.SaveChangesAsync();

        // And the one that actually needs reaping.
        var (abandoned, abandonedReservation) = await ReserveAsync(db, variantId, 3, Now.AddMinutes(-5));

        Assert.Equal((9, 7), await LedgerAsync(variantId));

        var (reaper, _, provider) = NewReaper(batchSize: 1);
        await using var _ = provider;

        Assert.Equal(3, await reaper.SweepAsync(CancellationToken.None));

        // The abandoned order's units came back...
        var (abandonedStatus, abandonedReservationStatus) = await StateAsync(abandoned.Id, abandonedReservation.Id);
        Assert.Equal(OrderStatus.Cancelled, abandonedStatus);
        Assert.Equal(ReservationStatus.Released, abandonedReservationStatus);

        // ...and the paid order's stayed exactly where they were.
        var (stuckStatus, stuckReservationStatus) = await StateAsync(stuck.Id, stuckReservation.Id);
        Assert.Equal(OrderStatus.Paid, stuckStatus);
        Assert.Equal(ReservationStatus.Held, stuckReservationStatus);

        Assert.Equal((9, 4), await LedgerAsync(variantId));
    }

    /// <summary>
    /// Cancelling an order releases EVERY line it holds, not only the lines whose window happened
    /// to have closed.
    /// <para>
    /// The inner query deliberately drops the <c>expires_at</c> filter that selects the order in the
    /// first place. Once the order is being cancelled its lines are not coming back, so a line left
    /// Held would strand those units on a shelf nobody can ever sell from — a Cancelled order
    /// holding stock, with no owner anywhere in the system to release it.
    /// </para>
    /// <para>
    /// A real checkout stamps every line with the same expiry, so this state is not one the shop
    /// produces today. It is asserted anyway because the code deliberately handles it, and because
    /// the old shape reached it by a route that no longer exists: the batch limit used to count
    /// reservations, so one order's lines could split across two sweeps and the first could cancel
    /// the order while the second still held stock for it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cancelling_an_order_releases_every_line_it_holds_not_only_the_lapsed_ones()
    {
        await using var db = fixture.CreateContext();

        var lapsedVariant = await StockAsync(db, onHand: 5);
        var freshVariant = await StockAsync(db, onHand: 5);

        var (order, lapsedReservation) = await ReserveAsync(db, lapsedVariant, 2, Now.AddMinutes(-5));

        // A second line on the SAME order whose window has not closed.
        var freshReservation = new StockReservation(freshVariant, order.Id, 3, Now.AddMinutes(30));
        db.StockReservations.Add(freshReservation);

        Assert.Equal(1, await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE stock_items
             SET reserved = reserved + 3
             WHERE variant_id = {freshVariant}
               AND deleted_at IS NULL
               AND on_hand - reserved >= 3
             """));

        await db.SaveChangesAsync();

        Assert.Equal((5, 2), await LedgerAsync(lapsedVariant));
        Assert.Equal((5, 3), await LedgerAsync(freshVariant));

        var (reaper, _, provider) = NewReaper();
        await using var _ = provider;

        // Five units back, not two: the lapsed line is what made the order a candidate, but the
        // cancellation takes the whole order with it.
        Assert.Equal(5, await reaper.SweepAsync(CancellationToken.None));

        Assert.Equal((5, 0), await LedgerAsync(lapsedVariant));
        Assert.Equal((5, 0), await LedgerAsync(freshVariant));

        var (status, lapsedStatus) = await StateAsync(order.Id, lapsedReservation.Id);
        var (_, freshStatus) = await StateAsync(order.Id, freshReservation.Id);

        Assert.Equal(OrderStatus.Cancelled, status);
        Assert.Equal(ReservationStatus.Released, lapsedStatus);
        Assert.Equal(ReservationStatus.Released, freshStatus);
    }

    /// <summary>
    /// The reaper is only worth anything if the composed host registers it.
    /// <para>
    /// Every other test in this file constructs a reaper by hand and drives <c>SweepAsync</c>, which
    /// is the right way to test the logic and no way at all to notice that nobody runs it. The cart
    /// endpoints once shipped unmapped for exactly this shape of reason — a slice fully built, fully
    /// tested and never composed — and a reaper registered nowhere fails silently and permanently:
    /// abandoned checkouts hold their units for good and the shop stops selling, with no error.
    /// </para>
    /// <para>
    /// <b>It is asserted against the real host, and it was not.</b> This test used to build its own
    /// <c>ServiceCollection</c>, call <c>AddCheckout()</c> on it and assert on that — which proves
    /// the extension method does what it says and nothing about the composition root, so deleting
    /// the registration from <c>Program.cs</c> left it green.
    /// </para>
    /// </summary>
    [Fact]
    public void The_composed_host_registers_the_reaper_so_something_actually_sweeps()
    {
        using var host = new CheckoutHost(fixture.ConnectionString);

        // AddHostedService registers under IHostedService rather than the concrete type, so the
        // assertion has to look through what the host would actually start.
        var hosted = host.Services.GetServices<IHostedService>().ToList();

        Assert.True(
            hosted.Exists(service => service is ReservationReaper),
            "The composed host registers no ReservationReaper, so nothing reclaims stock from "
            + "abandoned checkouts. Program.cs needs builder.Services.AddCheckout(builder.Configuration). "
            + $"Hosted services found: {string.Join(", ", hosted.Select(service => service.GetType().Name))}");
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
