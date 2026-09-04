using System.Net;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence behind the claim that a double-submitted checkout creates one order.
/// <para>
/// The mechanism under test is a unique index on <c>(demo_session_id, idempotency_key)</c> and a
/// <c>catch</c> around the insert that loses gracefully. The mechanism a reviewer expects — read
/// first, and only insert if nothing is there — is the bug: two simultaneous submits both read
/// nothing and both insert, which is exactly the case a shopper produces by double-clicking. So
/// these tests submit the same key twice <em>at the same moment</em> as well as in sequence, and
/// the concurrent case is the one that would have caught the version everyone writes first.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CheckoutIdempotencyTests : IDisposable
{
    private readonly Storefront _shop;

    public CheckoutIdempotencyTests(PostgresFixture fixture) => _shop = new Storefront(fixture);

    /// <summary>Disposes the host, its clients and the in-memory key ring.</summary>
    public void Dispose() => _shop.Dispose();

    /// <summary>
    /// Two submits with one key, released together: one order, one order number, one charge, and a
    /// second answer that hands back the first one's order rather than an error.
    /// <para>
    /// The two status codes differ deliberately, and the difference is worth defending. The submit
    /// that created the order answers 201 with a <c>Location</c>; the one that lost answers for the
    /// order as it actually stands. A client that treats every 2xx as success is correct either way,
    /// and a client that cares can tell whether it was the one that placed the order — which matters
    /// for analytics far more than it matters for the shopper.
    /// </para>
    /// <para>
    /// <strong>The loser's code is 200 or 202, and this test may not insist on which.</strong> The
    /// order row commits when it is placed; the money settles in a second transaction after the
    /// gateway has answered. So the losing submit re-reads somewhere inside that gap and reports
    /// what it finds — 200 for an order already Paid, 202 for one still settling. Both are the
    /// truth, and the replay branch returns the truth on purpose rather than a flat 200 that once
    /// told four different non-successes they had succeeded. Asserting 200 alone was really
    /// asserting that the winner's gateway call finished before the loser's re-read, which is a
    /// property of how fast the machine is rather than of how the shop behaves.
    /// </para>
    /// <para>
    /// The shelf holds five units rather than one on purpose. With a single unit the losing submit
    /// would be refused for stock before it ever reached the unique index, which is also correct
    /// and is a different test; here both submits reserve successfully, so the index is genuinely
    /// the thing deciding the outcome. That the ledger ends at one — not two — is the proof the
    /// loser gave its reservation back on the way out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_double_clicked_checkout_creates_one_order_and_reserves_one_unit()
    {
        var sextant = await _shop.StockAsync("Brass sextant", onHand: 5);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(sextant);

        var key = $"double-click-{Guid.CreateVersion7():N}";

        var responses = await Storefront.AllAtOnceAsync(2, _ => shopper.CheckoutAsync(key));

        var statuses = new List<HttpStatusCode>();
        var orders = new List<OrderView>();

        foreach (var response in responses)
        {
            using (response)
            {
                statuses.Add(response.StatusCode);

                Assert.True(
                    response.IsSuccessStatusCode,
                    $"A double-submitted checkout answered {(int)response.StatusCode}: "
                    + await response.Content.ReadAsStringAsync());

                orders.Add(await ResponseReader.OrderAsync(response));
            }
        }

        Assert.Contains(HttpStatusCode.Created, statuses);

        var loser = Assert.Single(statuses, status => status != HttpStatusCode.Created);

        Assert.True(
            loser is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"The losing submit answered {(int)loser}. It must replay the winner's order as 200 if "
            + "settlement has already committed, or 202 if it is still in flight - never a second "
            + "201, and never a failure.");

        // Both callers were told about the same order, so neither storefront shows a number the
        // other one cannot look up.
        Assert.Single(orders.Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal));

        var row = Assert.Single(await _shop.OrdersForAsync(sextant));
        Assert.Equal(orders[0].OrderNumber, row.OrderNumber);
        Assert.Equal(key, row.IdempotencyKey);
        Assert.Equal("Paid", row.Status);

        // One unit promised, not two: the losing submit's reservation went back with its rollback.
        Assert.Equal(new Ledger(OnHand: 5, Reserved: 1), await _shop.LedgerAsync(sextant));
        Assert.Equal("Confirmed", Assert.Single(await _shop.ReservationsForAsync(sextant)).Status);
    }

    /// <summary>
    /// The same key sent again a moment later returns the original order, and does not ask the
    /// payment gateway a second time.
    /// <para>
    /// Two things make this more than a repeat of the concurrent case. The first is that placing an
    /// order <em>empties the cart</em>, so a replay arriving after the fact has nothing to check
    /// out; without the key it would be refused as an empty cart and the shopper who retried a
    /// timed-out request would be told their order does not exist. The second is the null payment
    /// block on the replay's response: the gateway is not asked again, so there is no second
    /// authorization to reconcile, and the absence of that block is the only place a test can
    /// observe it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_replayed_checkout_returns_the_original_order_without_charging_again()
    {
        var chronometer = await _shop.StockAsync("Ship's chronometer", onHand: 5);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(chronometer, 2);

        var key = $"replay-{Guid.CreateVersion7():N}";

        OrderView placed;
        using (var first = await shopper.CheckoutAsync(key))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            placed = await ResponseReader.OrderAsync(first);
        }

        Assert.NotNull(placed.Payment);
        Assert.True(placed.Payment!.Captured);
        Assert.Equal(0, (await shopper.CartAsync()).TotalQuantity);

        using (var replay = await shopper.CheckoutAsync(key))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            var again = await ResponseReader.OrderAsync(replay);

            Assert.Equal(placed.OrderNumber, again.OrderNumber);
            Assert.Equal(placed.Total.Amount, again.Total.Amount);
            Assert.Equal(placed.Captured.Amount, again.Captured.Amount);
            Assert.Equal("Paid", again.Status);

            // No second authorization: a replay is answered from the order, and the gateway is
            // never told about it.
            Assert.Null(again.Payment);
        }

        var row = Assert.Single(await _shop.OrdersForAsync(chronometer));
        Assert.Equal(2, row.Quantity);
        Assert.Equal(placed.Total.Amount, row.CapturedAmount);

        // Two units, charged once. A replay that had re-run the reservation would show four.
        Assert.Equal(new Ledger(OnHand: 5, Reserved: 2), await _shop.LedgerAsync(chronometer));
    }

    /// <summary>
    /// Two visitors may both use the key <c>1</c>, and get two orders.
    /// <para>
    /// The index is scoped to the session, which is what makes an unguessable key unnecessary. If
    /// it were global, one shopper picking an obvious key would silently hand another shopper's
    /// order back to them — a leak far worse than a duplicate charge, and one that would look like
    /// idempotency working.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_visitors_idempotency_key_cannot_collide_with_anothers()
    {
        var compass = await _shop.StockAsync("Binnacle compass", onHand: 5);

        var first = await _shop.NewShopperAsync();
        var second = await _shop.NewShopperAsync();

        await first.AddToCartAsync(compass);
        await second.AddToCartAsync(compass);

        const string SharedKey = "1";

        using var firstResponse = await first.CheckoutAsync(SharedKey);
        using var secondResponse = await second.CheckoutAsync(SharedKey);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);

        var firstOrder = await ResponseReader.OrderAsync(firstResponse);
        var secondOrder = await ResponseReader.OrderAsync(secondResponse);

        Assert.NotEqual(firstOrder.OrderNumber, secondOrder.OrderNumber);

        var rows = await _shop.OrdersForAsync(compass);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.DemoSessionId).Distinct().Count());
        Assert.All(rows, row => Assert.Equal(SharedKey, row.IdempotencyKey));

        Assert.Equal(new Ledger(OnHand: 5, Reserved: 2), await _shop.LedgerAsync(compass));
    }
}
