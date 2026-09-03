using System.Net;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence behind the claim that this shop does not oversell.
/// <para>
/// The claim is easy to make and almost as easy to appear to demonstrate. A test that checks out
/// twice in a row against one unit passes against a completely broken implementation, because the
/// second read sees the first write; a test that runs two overlapping requests against an in-memory
/// database passes for the same reason. The only thing that can be wrong here is a race, and a race
/// is only visible when two requests are genuinely inside the same critical section at the same
/// moment, against a database that has its own opinion about locking. So every test below runs real
/// concurrent HTTP requests through the composed host against real PostgreSQL, and each one is
/// named after the commercial rule it is protecting rather than after the code it happens to touch.
/// </para>
/// <para>
/// What they are all really testing is one line of SQL. Checkout reserves stock with
/// <c>UPDATE stock_items SET reserved = reserved + q WHERE variant_id = v AND on_hand - reserved
/// &gt;= q</c> and believes the row count. The alternative — load the aggregate, ask
/// <c>StockItem.TryReserve</c>, save — is the version a reviewer would expect to find, reads better,
/// and is wrong: two requests holding two copies of the same row both pass the in-memory check.
/// <see cref="Fifty_shoppers_racing_for_five_units_sell_exactly_five"/> is the test that tells the
/// difference between the two, and it was confirmed to fail against the read-then-write version
/// before being trusted to pass against this one.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CheckoutStockRaceTests : IDisposable
{
    private readonly Storefront _shop;

    public CheckoutStockRaceTests(PostgresFixture fixture) => _shop = new Storefront(fixture);

    /// <summary>Disposes the host, its clients and the in-memory key ring.</summary>
    public void Dispose() => _shop.Dispose();

    /// <summary>
    /// Fifty shoppers, five units, one instant: five orders and forty-five refusals.
    /// <para>
    /// This is the headline. Every number in it is asserted exactly — not "at most five", which a
    /// shop that sold nothing would satisfy, and not "some conflicts", which a shop that refused
    /// everybody would satisfy. The three assertions that matter are that exactly five payments
    /// were taken, that exactly forty-five shoppers were told no, and that nobody was shown a 500:
    /// losing a race is a normal commercial outcome and must arrive as a 409 that names the item,
    /// not as a server error and not as a constraint violation leaking out of the database.
    /// </para>
    /// <para>
    /// The ledger assertion at the end is the one that would catch an oversell that somehow
    /// answered every caller politely. <c>reserved</c> finishing at five against <c>on_hand</c> of
    /// five means five units are promised and none are double-promised; the database's own
    /// <c>ck_stock_items_reserved_within_on_hand</c> check would have refused a sixth, which is why
    /// a failure here shows up as a 500 in the loop above rather than as a wrong number below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Fifty_shoppers_racing_for_five_units_sell_exactly_five()
    {
        const int Units = 5;
        const int Shoppers = 50;

        var jib = await _shop.StockAsync("Storm jib", onHand: Units);

        var shoppers = await _shop.NewShoppersAsync(Shoppers);
        await Task.WhenAll(shoppers.Select(shopper => shopper.AddToCartAsync(jib)));

        var responses = await Storefront.AllAtOnceAsync(
            Shoppers,
            index => shoppers[index].CheckoutAsync($"race-{index}-{Guid.CreateVersion7():N}"));

        var placed = new List<OrderView>();
        var refused = new List<ProblemView>();
        var unexpected = new List<string>();

        foreach (var response in responses)
        {
            using (response)
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Created:
                        placed.Add(await ResponseReader.OrderAsync(response));
                        break;

                    case HttpStatusCode.Conflict:
                        refused.Add(await ResponseReader.ProblemAsync(response));
                        break;

                    default:
                        unexpected.Add(
                            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                        break;
                }
            }
        }

        // Reported first and in full. Any status other than 201 or 409 here means the race was
        // resolved by something other than the guarded UPDATE — a constraint violation, a deadlock,
        // a timeout — and the body is the only thing that says which.
        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} of {Shoppers} checkouts answered with neither 201 nor 409. Losing a "
            + "race for stock is an ordinary outcome and must be reported as one:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unexpected));

        Assert.Equal(Units, placed.Count);
        Assert.Equal(Shoppers - Units, refused.Count);

        // Five orders, not one order counted five times.
        Assert.Equal(Units, placed.Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal).Count());
        Assert.All(placed, order =>
        {
            Assert.Equal("Paid", order.Status);
            Assert.Equal(order.Total.Amount, order.Captured.Amount);
            Assert.Equal(1, Assert.Single(order.Lines).Quantity);
        });

        // Every refusal names the variant that ran out, so a storefront can highlight the row
        // rather than apologising in general terms.
        Assert.All(refused, problem =>
        {
            Assert.Equal((int)HttpStatusCode.Conflict, problem.Status);
            Assert.NotNull(problem.Shortfall);
            Assert.Equal(jib.VariantId, problem.Shortfall!.VariantId);
            Assert.Equal(jib.Sku, problem.Shortfall.Sku);
            Assert.Equal(1, problem.Shortfall.Requested);
            Assert.Equal(0, problem.Shortfall.Available);
        });

        // The database's own account of what happened, read outside every session.
        Assert.Equal(new Ledger(OnHand: Units, Reserved: Units), await _shop.LedgerAsync(jib));

        var orders = await _shop.OrdersForAsync(jib);
        Assert.Equal(Units, orders.Count);
        Assert.Equal(Units, orders.Sum(order => order.Quantity));
        Assert.Equal(Units, orders.Select(order => order.DemoSessionId).Distinct().Count());

        var reservations = await _shop.ReservationsForAsync(jib);
        Assert.Equal(Units, reservations.Count);
        Assert.All(reservations, reservation => Assert.Equal("Confirmed", reservation.Status));
    }

    /// <summary>
    /// Two shoppers reach for the last unit together. One gets it; the other is told which item
    /// they lost and how many were left, and is told it in a way a screen can render.
    /// <para>
    /// The 409 body is asserted field by field on purpose. "Something went wrong, try again" is the
    /// failure mode this endpoint is designed against: the shopper needs to know it was this line,
    /// this SKU, and that the answer is not going to be different if they press the button again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_shoppers_racing_for_the_last_unit_are_told_which_one_ran_out()
    {
        var porthole = await _shop.StockAsync("Bronze porthole", onHand: 1);

        var first = await _shop.NewShopperAsync();
        var second = await _shop.NewShopperAsync();

        await first.AddToCartAsync(porthole);
        await second.AddToCartAsync(porthole);

        var responses = await Storefront.AllAtOnceAsync(
            2,
            index => (index == 0 ? first : second).CheckoutAsync($"last-unit-{index}-{Guid.CreateVersion7():N}"));

        var statuses = new List<HttpStatusCode>();
        OrderView? sold = null;
        ProblemView? lost = null;

        foreach (var response in responses)
        {
            using (response)
            {
                statuses.Add(response.StatusCode);

                if (response.StatusCode is HttpStatusCode.Created)
                {
                    sold = await ResponseReader.OrderAsync(response);
                }
                else if (response.StatusCode is HttpStatusCode.Conflict)
                {
                    lost = await ResponseReader.ProblemAsync(response);
                }
            }
        }

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            statuses.Order().ToArray());

        Assert.NotNull(sold);
        Assert.Equal("Paid", sold!.Status);

        Assert.NotNull(lost);
        Assert.NotNull(lost!.Shortfall);
        Assert.Equal(porthole.VariantId, lost.Shortfall!.VariantId);
        Assert.Equal(porthole.Sku, lost.Shortfall.Sku);
        Assert.Equal(1, lost.Shortfall.Requested);
        Assert.Equal(0, lost.Shortfall.Available);

        // The loser's refusal mentions the item by name, because the detail line is what the
        // storefront shows when it has nowhere structured to put the shortfall.
        Assert.Contains(porthole.Sku, lost.Detail ?? string.Empty, StringComparison.Ordinal);

        Assert.Equal(new Ledger(OnHand: 1, Reserved: 1), await _shop.LedgerAsync(porthole));
        Assert.Equal(1, Assert.Single(await _shop.OrdersForAsync(porthole)).Quantity);
    }

    /// <summary>
    /// A cart whose second line cannot be filled buys nothing at all — and gives back the units it
    /// had already taken for the first line.
    /// <para>
    /// This is the failure that quietly costs a real shop money. Reserving line by line and
    /// abandoning halfway leaves stock promised to an order that will never exist, and nothing
    /// reports it: the units are simply unsellable until somebody notices the ledger drifting. Here
    /// the release is not a compensating step that could be forgotten — the reservations are
    /// uncommitted increments inside one transaction, so rolling back <em>is</em> the release.
    /// </para>
    /// <para>
    /// Which line the checkout reaches first is decided by variant id, because the reservation loop
    /// orders by it so that two shoppers buying the same two items in opposite cart order cannot
    /// deadlock. The roles below are therefore assigned after the ids exist, using the same
    /// comparison the loop uses — otherwise this test would only be testing the rollback about half
    /// the time, and would look flaky rather than wrong.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_checkout_that_cannot_fill_every_line_gives_back_what_it_had_already_taken()
    {
        var chain = await _shop.StockAsync("Anchor chain", onHand: 10);
        var cleat = await _shop.StockAsync("Deck cleat", onHand: 10);

        var (plentiful, exhausted) = Comparer<Guid>.Default.Compare(chain.VariantId, cleat.VariantId) < 0
            ? (chain, cleat)
            : (cleat, chain);

        // Another shopper is already holding three of the ten, so "back where it started" is a
        // number this test could get wrong rather than a zero it could stumble into.
        await _shop.ReserveElsewhereAsync(plentiful, 3);
        await _shop.ReserveElsewhereAsync(exhausted, 10);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(plentiful, 2);
        await shopper.AddToCartAsync(exhausted, 1);

        using (var response = await shopper.CheckoutAsync($"partial-{Guid.CreateVersion7():N}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var problem = await ResponseReader.ProblemAsync(response);
            Assert.NotNull(problem.Shortfall);
            Assert.Equal(exhausted.VariantId, problem.Shortfall!.VariantId);
            Assert.Equal(1, problem.Shortfall.Requested);
            Assert.Equal(0, problem.Shortfall.Available);
        }

        // Exactly what it was before the attempt: the two units this checkout reserved are gone
        // again, and the three somebody else is holding are untouched.
        Assert.Equal(new Ledger(OnHand: 10, Reserved: 3), await _shop.LedgerAsync(plentiful));

        Assert.Empty(await _shop.OrdersForAsync(plentiful));
        Assert.Empty(await _shop.ReservationsForAsync(plentiful));

        // And the units are genuinely free rather than merely reported as free. The next shopper
        // takes all seven that remain, which they could not do if two were still stranded.
        var next = await _shop.NewShopperAsync();
        await next.AddToCartAsync(plentiful, 7);

        using var sale = await next.CheckoutAsync($"after-partial-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);

        Assert.Equal(new Ledger(OnHand: 10, Reserved: 10), await _shop.LedgerAsync(plentiful));
    }

    /// <summary>
    /// A declined card gives the unit back, so the next shopper can buy the thing the first one
    /// could not pay for.
    /// <para>
    /// The interesting half is what is <em>not</em> undone. The order survives as Cancelled rather
    /// than being rolled away, because the attempt really happened and because that row is what
    /// keeps the idempotency key spent — without it a frantically re-clicked "Pay" mints a new
    /// order number, and therefore a new gateway reference, and therefore a second chance at a
    /// second real charge. The cart survives too, so the shopper can fix their card rather than
    /// rebuild their basket. Only the stock is returned, and it is returned by a guarded UPDATE
    /// that mirrors the one that took it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_declined_payment_releases_the_unit_for_the_next_shopper()
    {
        var lantern = await _shop.StockAsync("Storm lantern", onHand: 1);

        var unlucky = await _shop.NewShopperAsync();
        await unlucky.AddToCartAsync(lantern);

        string cancelledOrderNumber;

        using (var declined = await unlucky.CheckoutAsync($"declined-{Guid.CreateVersion7():N}", scenario: "Decline"))
        {
            Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);

            var problem = await ResponseReader.ProblemAsync(declined);
            Assert.NotNull(problem.Payment);
            Assert.Equal("Declined", problem.Payment!.Outcome);
            Assert.False(problem.Payment.Captured);
            Assert.NotNull(problem.Payment.DeclineReason);
            Assert.NotNull(problem.OrderNumber);

            cancelledOrderNumber = problem.OrderNumber!;
        }

        // The unit is back on the shelf, and the paperwork explaining where it went is not.
        Assert.Equal(new Ledger(OnHand: 1, Reserved: 0), await _shop.LedgerAsync(lantern));

        var cancelled = Assert.Single(await _shop.OrdersForAsync(lantern));
        Assert.Equal(cancelledOrderNumber, cancelled.OrderNumber);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(0, cancelled.CapturedAmount);

        Assert.Equal("Released", Assert.Single(await _shop.ReservationsForAsync(lantern)).Status);

        // The cart is untouched, so the shopper can try again with another card.
        Assert.Equal(1, (await unlucky.CartAsync()).TotalQuantity);

        // And the shop can sell the unit to somebody else, which is the whole point of releasing it.
        var next = await _shop.NewShopperAsync();
        await next.AddToCartAsync(lantern);

        using var sold = await next.CheckoutAsync($"after-decline-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);
        Assert.Equal("Paid", (await ResponseReader.OrderAsync(sold)).Status);

        Assert.Equal(new Ledger(OnHand: 1, Reserved: 1), await _shop.LedgerAsync(lantern));
    }
}
