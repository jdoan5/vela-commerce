using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Messaging;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Tenancy;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The purge deletes rows, which makes it the most dangerous worker in the solution and the one
/// whose tests have to be the most specific.
/// <para>
/// Every test here runs with <b>no demo session bound at all</b>, which is not a convenience — it is
/// the deployment's condition. The tenancy filter fails closed, so a purge that lost its
/// <c>IgnoreQueryFilters()</c> would see zero rows and report a cheerful, silent success. Every
/// assertion below that counts something deleted is therefore also an assertion that the filter is
/// being bypassed deliberately.
/// </para>
/// <para>
/// <b>The clock is in 2020, for the reason <c>ReservationReaperTests</c> gives and one that is
/// sharper here.</b> The assembly shares one container and a sweep is GLOBAL. The reaper's worst
/// case against a neighbour's fixture is a released reservation; this worker's worst case is a row
/// that no longer exists. With the clock at 2020-06-01 and a 24-hour window the cutoff is
/// 2020-05-31, so every row any other test file writes against the real clock is dated 2026 —
/// comfortably NEWER than the cutoff, and therefore untouchable by anything in this file.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DemoDataPurgeTests(PostgresFixture fixture)
{
    /// <summary>The instant every sweep in this file runs at. See the class remarks.</summary>
    private static readonly DateTimeOffset Now = new(2020, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Comfortably past the 24-hour window: everything dated here is expired.</summary>
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>An hour before <see cref="Now"/> — inside the window, and must survive.</summary>
    private static readonly DateTimeOffset JustNow = Now.AddHours(-1);

    private static readonly ShippingAddress Address = new()
    {
        Recipient = "Ada Lovelace",
        Line1 = "12 Marylebone Road",
        City = "London",
        PostalCode = "NW1 5LA",
        CountryCode = "GB"
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // =====================================================================================
    // Stock. The half of this worker that can permanently damage a shop strangers share.
    // =====================================================================================

    /// <summary>
    /// The headline: an expired order that is still holding units gives them back before it is
    /// deleted, and the ledger everybody shares is whole afterwards.
    /// <para>
    /// The order is <b>Paid</b> on purpose, and this is the case that separates the purge from
    /// <c>ReservationReaper</c>. The reaper refuses to release a Paid order's reservations, and it
    /// is right to — somebody bought those units and the order still exists to account for them.
    /// The purge is about to delete the order, so units held against a row that is about to vanish
    /// are not spoken for, they are lost. Delete without releasing and <c>reserved</c> stays at 4
    /// forever, on a global row, with nothing left in the database to explain why.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_expired_paid_order_returns_its_units_before_it_is_deleted()
    {
        await using var db = fixture.CreateContext();

        var variantId = await StockAsync(db, onHand: 10);
        var (order, reservation) = await ReserveAsync(db, variantId, quantity: 4, placedAt: LongAgo);

        // Settlement confirms the reservation and moves the order to Paid. The ledger still holds
        // the units — only shipping decrements them — which is precisely the trap.
        reservation.Confirm();
        order.MarkPaid(order.Total, "sim_paid_for_the_purge", LongAgo);
        await db.SaveChangesAsync();

        Assert.Equal((10, 4), await LedgerAsync(variantId));

        var summary = await NewPurge().SweepAsync(CancellationToken.None);

        Assert.Equal(1, summary.Orders);
        Assert.Equal(1, summary.Reservations);
        Assert.Equal(4, summary.Units);

        Assert.Equal((10, 0), await LedgerAsync(variantId));
        Assert.False(await OrderExistsAsync(order.Id));
        Assert.Equal(0, await ReservationCountAsync(order.Id));
    }

    /// <summary>
    /// A shipped order's units are NOT handed back, because they already left the building — and
    /// the proof is that the NEXT shopper's reservation is still intact afterwards.
    /// <para>
    /// This test was written a weaker way first and the mutation exposed it. The first version
    /// shipped one order, purged it, and asserted the ledger was unchanged. It passed with
    /// <see cref="OrderStatus.Shipped"/> wrongly added to <c>OrderStateMachine.HoldingStock</c>,
    /// because the release runs into <c>AND reserved &gt;= quantity</c>, finds the ledger holding
    /// nothing, refuses, and leaves exactly the numbers the test was checking. The guard hid the
    /// bug from the test — which is the second time in this file's history that a safety mechanism
    /// made a wrong thing look right.
    /// </para>
    /// <para>
    /// So the scenario is the one the harm actually needs: a shipped order to purge, and a SECOND
    /// live order holding units on the same variant. <c>OrderTimelineWorker</c> ships by
    /// decrementing <c>reserved</c> and <c>on_hand</c> together and leaves the reservation row
    /// <em>Confirmed</em>, not Released, so a purge that treated Shipped as "still holding" would
    /// release three units that nobody is holding — and the guard would happily allow it, because
    /// the ledger IS holding three: somebody else's. The count never goes negative and no
    /// constraint trips. The second shopper's stock is simply gone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_expired_shipped_order_does_not_release_the_next_shoppers_units()
    {
        await using var db = fixture.CreateContext();

        var variantId = await StockAsync(db, onHand: 10);

        // The order to be purged: shipped, expired, holding nothing.
        var (shipped, shippedReservation) = await ReserveAsync(db, variantId, quantity: 3, placedAt: LongAgo);

        shippedReservation.Confirm();
        shipped.MarkPaid(shipped.Total, "sim_shipped_for_the_purge", LongAgo);
        shipped.MarkPacked();
        shipped.MarkShipped();
        await db.SaveChangesAsync();

        // Shipping as the timeline worker does it: both columns down, reservation left Confirmed.
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE stock_items
             SET on_hand = on_hand - 3, reserved = reserved - 3
             WHERE variant_id = {variantId}
             """);

        // The next shopper, inside the window, holding three units the purge must not touch.
        var (live, _) = await ReserveAsync(db, variantId, quantity: 3, placedAt: JustNow);

        Assert.Equal((7, 3), await LedgerAsync(variantId));

        var summary = await NewPurge().SweepAsync(CancellationToken.None);

        Assert.Equal(1, summary.Orders);
        Assert.Equal(0, summary.Units);

        // Nothing was released, so there was nothing to release: a shipped order's reservations are
        // already settled. This is the assertion the first version of this test was missing.
        Assert.Equal(0, summary.Reservations);

        // And the numbers that matter: the live order's three units are still reserved for it.
        Assert.Equal((7, 3), await LedgerAsync(variantId));
        Assert.False(await OrderExistsAsync(shipped.Id));
        Assert.True(await OrderExistsAsync(live.Id));
    }

    /// <summary>
    /// An order inside the window is left completely alone — the row, its reservation and its units.
    /// Without this, "delete everything" would pass every other test in this file.
    /// </summary>
    [Fact]
    public async Task An_order_inside_the_window_is_not_touched()
    {
        await using var db = fixture.CreateContext();

        var variantId = await StockAsync(db, onHand: 10);
        var (order, _) = await ReserveAsync(db, variantId, quantity: 2, placedAt: JustNow);

        var summary = await NewPurge().SweepAsync(CancellationToken.None);

        Assert.Equal(0, summary.Orders);
        Assert.True(await OrderExistsAsync(order.Id));
        Assert.Equal(1, await ReservationCountAsync(order.Id));
        Assert.Equal((10, 2), await LedgerAsync(variantId));
    }

    // =====================================================================================
    // Carts, aged by their own primary key because there is no other column to age them by.
    // =====================================================================================

    /// <summary>
    /// A cart is expired by the timestamp inside its UUIDv7 id, and a cart minted an hour ago is
    /// not. The two together are what make this an age predicate rather than a truncate.
    /// </summary>
    [Fact]
    public async Task Carts_expire_by_the_timestamp_inside_their_own_id()
    {
        var expired = await CartAsync(mintedAt: LongAgo);
        var fresh = await CartAsync(mintedAt: JustNow);

        var summary = await NewPurge().SweepAsync(CancellationToken.None);

        Assert.True(summary.Carts >= 1);
        Assert.False(await CartExistsAsync(expired));
        Assert.True(await CartExistsAsync(fresh));
    }

    /// <summary>
    /// <b>The fail-closed case, and the reason ageing a row by its key is defensible at all.</b>
    /// <para>
    /// <c>uuid_extract_timestamp()</c> returns NULL for any id that is not a v1 or v7 UUID, and
    /// <c>NULL &lt; cutoff</c> is NULL rather than true — so a row whose age cannot be established
    /// is a row that is kept, not one that is deleted. The failure direction of an unexpected id is
    /// "lives forever", never "deleted early", and a purge is a worker where that asymmetry is the
    /// whole safety argument.
    /// </para>
    /// <para>
    /// This is not hypothetical: <c>DemoTenancyQueryFilterTests</c> inserts rows with
    /// <c>gen_random_uuid()</c>, which is a v4, and they are dated 2026 in a table this sweep reads
    /// globally.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cart_whose_id_is_not_a_uuidv7_is_never_expired()
    {
        await using var db = fixture.CreateContext();

        // Guid.NewGuid() is a v4 — the id a fixture that did not know about this design would
        // write, and the one DemoTenancyQueryFilterTests actually does write via gen_random_uuid().
        var undateable = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO carts (id, demo_session_id, currency)
             VALUES ({undateable}, {Guid.CreateVersion7()}, 'USD')
             """);

        Assert.Equal(4, undateable.Version);

        await NewPurge().SweepAsync(CancellationToken.None);

        Assert.True(
            await CartExistsAsync(undateable),
            "A cart whose id carries no timestamp was deleted. uuid_extract_timestamp returns NULL "
            + "for a non-v7 id, so this row's age is unknowable — and a purge that deletes what it "
            + "cannot date fails in the one direction that cannot be undone.");
    }

    // =====================================================================================
    // Price overlays and the outbox.
    // =====================================================================================

    /// <summary>
    /// Overlays expire by <c>updated_at</c>, not <c>created_at</c>. An overlay a demo admin
    /// repriced an hour ago is in use however long ago it was first written, and swapping the two
    /// columns — which reads like a harmless equivalence — deletes it out from under them.
    /// </summary>
    [Fact]
    public async Task Price_overlays_expire_by_when_they_were_last_changed_not_first_created()
    {
        await using var db = fixture.CreateContext();

        var stale = await OverrideAsync(db, createdAt: LongAgo, updatedAt: LongAgo);
        var repriced = await OverrideAsync(db, createdAt: LongAgo, updatedAt: JustNow);

        var summary = await NewPurge().SweepAsync(CancellationToken.None);

        Assert.True(summary.PriceOverrides >= 1);
        Assert.False(await OverrideExistsAsync(stale));
        Assert.True(await OverrideExistsAsync(repriced));
    }

    /// <summary>
    /// Settled outbox messages are swept; a Pending one is not, however old it is.
    /// <para>
    /// A Pending row is undelivered work the dispatcher is still going to attempt. Deleting one
    /// would drop a notification silently, which is the exact failure the outbox pattern exists to
    /// make impossible — so an old Pending message is a bug to go and find, not a row to tidy away.
    /// Widening the predicate to "everything older than the cutoff" is the one-word change this
    /// test exists to catch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Only_settled_outbox_messages_are_swept()
    {
        var pending = await OutboxAsync(OutboxMessageStatus.Pending, LongAgo);
        var delivered = await OutboxAsync(OutboxMessageStatus.Delivered, LongAgo);
        var abandoned = await OutboxAsync(OutboxMessageStatus.Abandoned, LongAgo);
        var recent = await OutboxAsync(OutboxMessageStatus.Delivered, JustNow);

        var summary = await NewPurge().SweepAsync(CancellationToken.None);

        Assert.True(summary.OutboxMessages >= 2);

        Assert.True(await OutboxExistsAsync(pending), "A Pending outbox message was deleted.");
        Assert.False(await OutboxExistsAsync(delivered));
        Assert.False(await OutboxExistsAsync(abandoned));
        Assert.True(await OutboxExistsAsync(recent));
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>
    /// Builds a purge over the test container, with the retry strategy configured exactly as the
    /// real host configures it. A retrying execution strategy refuses a user-initiated transaction
    /// unless the whole unit is handed to it, and <c>PurgeOrderAsync</c> is shaped around that — a
    /// test host without retries would exercise a code path the deployment does not have.
    /// </summary>
    private DemoDataPurge NewPurge(int batchSize = 100)
    {
        var provider = new ServiceCollection()
            .AddDbContext<VelaCommerceDbContext>(options => options.UseNpgsql(
                fixture.ConnectionString,
                npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)))
            .BuildServiceProvider();

        // Enabled is irrelevant and deliberately left at its default: every test drives SweepAsync
        // directly, and the flag only gates the timer loop in ExecuteAsync.
        return new DemoDataPurge(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new DemoDataPurgeOptions { BatchSize = batchSize },
            new FixedClock(Now),
            NullLogger<DemoDataPurge>.Instance);
    }

    /// <summary>
    /// A UUIDv7 whose embedded timestamp is a value this test chose, which is the only way to write
    /// a row that is old without waiting a day for it to become old.
    /// <para>
    /// RFC 9562 layout, big-endian: the first 48 bits are Unix milliseconds, the high nibble of
    /// byte 6 is the version and the top two bits of byte 8 are the variant. Both are set
    /// explicitly — an id with the right timestamp and the wrong version is exactly the row
    /// <see cref="A_cart_whose_id_is_not_a_uuidv7_is_never_expired"/> proves is kept, so getting
    /// this wrong would make the ageing tests pass for the wrong reason.
    /// </para>
    /// </summary>
    private static Guid IdMintedAt(DateTimeOffset when)
    {
        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes);

        var milliseconds = when.ToUnixTimeMilliseconds();

        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;

        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));

        return new Guid(bytes, bigEndian: true);
    }

    private async Task<Guid> StockAsync(VelaCommerceDbContext db, int onHand)
    {
        var slug = $"purge-{Guid.CreateVersion7():N}";
        var product = new Product(slug, "Storm Jib", "Seeded by a purge test.", "rope-and-rigging");
        var variant = product.AddVariant($"PRG-{Guid.NewGuid():N}"[..18], "Standard", new Money(4_200));

        db.Products.Add(product);
        db.StockItems.Add(new StockItem(variant.Id, onHand));
        await db.SaveChangesAsync();

        return variant.Id;
    }

    /// <summary>
    /// Places an order the way checkout does — an aggregate built from a cart, a reservation row and
    /// the ledger incremented to match — at a moment this test chooses.
    /// </summary>
    private async Task<(Order Order, StockReservation Reservation)> ReserveAsync(
        VelaCommerceDbContext db,
        Guid variantId,
        int quantity,
        DateTimeOffset placedAt)
    {
        var cart = new Cart(Guid.CreateVersion7());
        cart.AddItem(variantId, "PRG-SKU", "Storm Jib", new Money(4_200), quantity);

        var order = Order.FromCart(
            cart,
            $"VELA-{Guid.NewGuid().ToString("N")[..7].ToUpperInvariant()}",
            $"purge-{Guid.CreateVersion7():N}",
            Address,
            Money.Zero(),
            Money.Zero(),
            placedAt);

        var reservation = new StockReservation(variantId, order.Id, quantity, placedAt.AddMinutes(15));

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

    /// <summary>A cart written by INSERT, because its id has to be one this test minted.</summary>
    private async Task<Guid> CartAsync(DateTimeOffset mintedAt)
    {
        await using var db = fixture.CreateContext();

        var id = IdMintedAt(mintedAt);

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO carts (id, demo_session_id, currency)
             VALUES ({id}, {Guid.CreateVersion7()}, 'USD')
             """);

        return id;
    }

    private async Task<(Guid Session, Guid Variant)> OverrideAsync(
        VelaCommerceDbContext db,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var session = Guid.CreateVersion7();
        var variantId = await StockAsync(db, onHand: 1);

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO demo_catalog_price_overrides
                 (demo_session_id, variant_id, price_amount, created_at, updated_at)
             VALUES ({session}, {variantId}, 999, {createdAt}, {updatedAt})
             """);

        return (session, variantId);
    }

    private async Task<Guid> OutboxAsync(OutboxMessageStatus status, DateTimeOffset createdAt)
    {
        await using var db = fixture.CreateContext();

        var id = Guid.CreateVersion7();

        // The payload is a parameter rather than a literal because an empty JSON object inside an
        // interpolated raw string needs a different number of dollar signs to say so, and a
        // parameter is clearer than winning that argument.
        const string payload = "{}";

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO outbox_messages
                 (id, message_type, payload, signature_header, deliver_after, attempts, status,
                  created_at, updated_at)
             VALUES ({id}, 'purge.test', {payload}, 'sha256=none', {createdAt}, 0, {(int)status},
                     {createdAt}, {createdAt})
             """);

        return id;
    }

    private async Task<(int OnHand, int Reserved)> LedgerAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();

        var item = await db.StockItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(entity => entity.VariantId == variantId);

        return (item.OnHand, item.Reserved);
    }

    private async Task<bool> OrderExistsAsync(Guid orderId) =>
        await CountAsync($"SELECT count(*)::int AS \"Value\" FROM orders WHERE id = '{orderId}'") > 0;

    private async Task<int> ReservationCountAsync(Guid orderId) =>
        await CountAsync($"SELECT count(*)::int AS \"Value\" FROM stock_reservations WHERE order_id = '{orderId}'");

    private async Task<bool> CartExistsAsync(Guid cartId) =>
        await CountAsync($"SELECT count(*)::int AS \"Value\" FROM carts WHERE id = '{cartId}'") > 0;

    private async Task<bool> OutboxExistsAsync(Guid id) =>
        await CountAsync($"SELECT count(*)::int AS \"Value\" FROM outbox_messages WHERE id = '{id}'") > 0;

    private async Task<bool> OverrideExistsAsync((Guid Session, Guid Variant) key) =>
        await CountAsync(
            "SELECT count(*)::int AS \"Value\" FROM demo_catalog_price_overrides "
            + $"WHERE demo_session_id = '{key.Session}' AND variant_id = '{key.Variant}'") > 0;

    /// <summary>
    /// Counts rows with the filters off and no session, which is the only way to ask "does this row
    /// exist" of a table whose tenancy filter fails closed. The ids interpolated here are Guids
    /// this test minted a moment ago, never input.
    /// </summary>
    private async Task<int> CountAsync(string sql)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }
}
