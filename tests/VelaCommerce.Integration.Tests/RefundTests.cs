using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence behind the claim that money can always be given back, and never given back twice.
/// <para>
/// These run against the composed host and real PostgreSQL for the same reason the stock-race tests
/// do: everything interesting here is a race or a rollback, and neither is observable against an
/// in-memory store. The refund path holds a row lock across a gateway call and writes its ledger
/// row only afterwards, and both of those decisions are only worth anything if a concurrent request
/// and a refusing gateway actually meet them.
/// </para>
/// <para>
/// <b>The ordering these tests exist to protect.</b> A ledger row means money moved. Every failure
/// path — an unreachable gateway, a refusing gateway, an amount larger than what is left — must
/// therefore leave the tables exactly as they were, because the alternative is a receipt that
/// promises a shopper money they will never receive. Several tests below assert on nothing having
/// happened, which is the assertion that would go missing first.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RefundTests : IDisposable
{
    private readonly Storefront _shop;

    public RefundTests(PostgresFixture fixture) => _shop = new Storefront(fixture);

    public void Dispose() => _shop.Dispose();

    /// <summary>Buys one unit and returns the paid order's number and what it cost.</summary>
    private async Task<(Shopper Shopper, string OrderNumber, long Captured, string Token)> BuyAsync(
        string productName,
        int onHand = 5,
        int quantity = 1)
    {
        var variant = await _shop.StockAsync(productName, onHand);
        var shopper = await _shop.NewShopperAsync();

        await shopper.AddToCartAsync(variant, quantity);

        using var response = await shopper.CheckoutAsync($"buy-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var order = await ResponseReader.OrderAsync(response);
        Assert.Equal("Paid", order.Status);

        return (shopper, order.OrderNumber, order.Captured.Amount, order.RetrievalToken);
    }

    [Fact]
    public async Task A_full_refund_returns_the_money_and_leaves_one_row_on_the_ledger()
    {
        var (shopper, orderNumber, captured, _) = await BuyAsync("Brass sextant");

        using var response = await shopper.RefundAsync(orderNumber, "refund-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var refund = await ResponseReader.RefundAsync(response);
        Assert.Equal(captured, refund.Refunded.Amount);
        Assert.Equal(0, refund.RefundableRemaining.Amount);
        Assert.True(refund.FullyRefunded);
        Assert.False(refund.Replayed);

        // The status is untouched. A refund is a movement of money, not of goods: collapsing a full
        // refund into Cancelled would lose the fact that a parcel is on its way.
        Assert.Equal("Paid", refund.Status);

        var ledger = await _shop.RefundsForAsync(orderNumber);
        var only = Assert.Single(ledger);
        Assert.Equal(captured, only.Amount);
        Assert.Equal("CustomerRequest", only.Reason);
        Assert.Equal("refund-1", only.IdempotencyKey);
        Assert.NotEmpty(only.GatewayReference);

        var money = await _shop.MoneyForAsync(orderNumber);
        Assert.Equal(captured, money.Refunded);
        Assert.Equal(0, money.Outstanding);
    }

    [Fact]
    public async Task A_retried_refund_carrying_the_same_key_returns_the_money_once()
    {
        var (shopper, orderNumber, captured, _) = await BuyAsync("Sailmaker's palm");

        using var first = await shopper.RefundAsync(orderNumber, "refund-1", amount: 1_000);
        using var again = await shopper.RefundAsync(orderNumber, "refund-1", amount: 1_000);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var replay = await ResponseReader.RefundAsync(again);

        // The retry is told the truth: it is being shown the first refund, not a second one.
        Assert.True(replay.Replayed);
        Assert.Equal(1_000, replay.Refunded.Amount);

        Assert.Single(await _shop.RefundsForAsync(orderNumber));

        var money = await _shop.MoneyForAsync(orderNumber);
        Assert.Equal(1_000, money.Refunded);
        Assert.Equal(captured - 1_000, money.Outstanding);
    }

    [Fact]
    public async Task Twenty_simultaneous_refunds_of_one_balance_return_it_exactly_once()
    {
        // The race this whole design is shaped around. Twenty requests, twenty distinct keys - so
        // idempotency cannot save it - each asking for the full outstanding balance at the same
        // instant. Without the row lock, several would read the same remaining balance, all pass the
        // "within what is left" check, and all reach the gateway before any of them wrote a row.
        var (shopper, orderNumber, captured, _) = await BuyAsync("Storm lantern");

        var responses = await Storefront.AllAtOnceAsync(
            20,
            index => shopper.RefundAsync(orderNumber, $"refund-{index}"));

        var accepted = responses.Count(response => response.StatusCode == HttpStatusCode.OK);
        var refused = responses.Count(response => response.StatusCode == HttpStatusCode.Conflict);

        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal(1, accepted);
        Assert.Equal(19, refused);

        var ledger = await _shop.RefundsForAsync(orderNumber);
        Assert.Single(ledger);
        Assert.Equal(captured, ledger[0].Amount);

        var money = await _shop.MoneyForAsync(orderNumber);
        Assert.Equal(captured, money.Refunded);
        Assert.Equal(0, money.Outstanding);
    }

    [Fact]
    public async Task A_gateway_that_refuses_leaves_no_ledger_row_and_no_money_moved()
    {
        // The reason the simulator can be told to say no. A handler that recorded the refund before
        // asking the gateway passes every other test in this file and fails only here - and in
        // production, only once it had already told somebody their money was on the way.
        var (shopper, orderNumber, captured, _) = await BuyAsync("Chart weight");

        using var response = await shopper.RefundAsync(
            orderNumber, "refund-1", scenarioHint: "refund-refused");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Empty(await _shop.RefundsForAsync(orderNumber));

        var money = await _shop.MoneyForAsync(orderNumber);
        Assert.Equal(0, money.Refunded);
        Assert.Equal(captured, money.Outstanding);
        Assert.Equal("Paid", money.Status);
    }

    [Fact]
    public async Task A_refused_refund_does_not_spend_its_idempotency_key()
    {
        // Nothing was recorded, so the same key must still work. A handler that reserved the key
        // before the gateway answered would leave a shopper permanently unable to be refunded by a
        // client that sensibly retries with the key it already generated.
        var (shopper, orderNumber, captured, _) = await BuyAsync("Tide table");

        using var refused = await shopper.RefundAsync(
            orderNumber, "refund-1", scenarioHint: "refund-refused");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        using var retried = await shopper.RefundAsync(orderNumber, "refund-1");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

        var refund = await ResponseReader.RefundAsync(retried);
        Assert.False(refund.Replayed);
        Assert.Equal(captured, refund.Refunded.Amount);
    }

    [Fact]
    public async Task Cancelling_a_paid_order_returns_the_money_and_puts_the_units_back()
    {
        // The gap this phase exists to close. Cancel used to take the Paid -> Cancelled edge and
        // leave the capture stranded, unreachable by any refund because a cancelled order refuses
        // them. Now the two happen together or not at all.
        var variant = await _shop.StockAsync("Ship's bell", onHand: 4);
        var shopper = await _shop.NewShopperAsync();

        await shopper.AddToCartAsync(variant, 3);

        using var placed = await shopper.CheckoutAsync($"buy-{Guid.CreateVersion7():N}");
        var order = await ResponseReader.OrderAsync(placed);

        var held = await _shop.LedgerAsync(variant);
        Assert.Equal(3, held.Reserved);

        using var response = await shopper.CancelAsync(order.OrderNumber, "cancel-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cancelled = await ResponseReader.RefundAsync(response);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(order.Captured.Amount, cancelled.Refunded.Amount);
        Assert.Equal(0, cancelled.RefundableRemaining.Amount);
        Assert.Equal(3, cancelled.RestockedUnits);

        var entry = Assert.Single(cancelled.Refunds);
        Assert.Equal("Cancellation", entry.Reason);
        Assert.Equal(3, entry.RestockedUnits);

        // The goods go back on the shelf: on-hand never moved, and the promise against it is gone.
        var after = await _shop.LedgerAsync(variant);
        Assert.Equal(4, after.OnHand);
        Assert.Equal(0, after.Reserved);
        Assert.Equal(4, after.Available);
    }

    [Fact]
    public async Task Cancelling_a_pending_order_releases_its_stock_and_records_no_refund()
    {
        // Nothing was captured, so there is nothing to give back and no ledger row to write. A
        // refund of zero would be a row asserting that no money moved.
        var variant = await _shop.StockAsync("Deck prism", onHand: 2);
        var shopper = await _shop.NewShopperAsync();

        await shopper.AddToCartAsync(variant, 2);

        using var placed = await shopper.CheckoutAsync($"buy-{Guid.CreateVersion7():N}", scenario: "Delay");
        Assert.Equal(HttpStatusCode.Accepted, placed.StatusCode);

        var order = await ResponseReader.OrderAsync(placed);
        Assert.Equal("Pending", order.Status);

        using var response = await shopper.CancelAsync(order.OrderNumber, "cancel-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cancelled = await ResponseReader.RefundAsync(response);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(2, cancelled.RestockedUnits);
        Assert.Empty(cancelled.Refunds);

        Assert.Empty(await _shop.RefundsForAsync(order.OrderNumber));

        var after = await _shop.LedgerAsync(variant);
        Assert.Equal(2, after.Available);
    }

    [Fact]
    public async Task Cancelling_a_shipped_order_is_refused_and_touches_nothing()
    {
        var (shopper, orderNumber, captured, _) = await BuyAsync("Sounding lead");
        await _shop.ShipAsync(orderNumber);

        using var response = await shopper.CancelAsync(orderNumber, "cancel-1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await ResponseReader.ProblemAsync(response);
        Assert.Contains("refunds", problem.Detail, StringComparison.Ordinal);

        var money = await _shop.MoneyForAsync(orderNumber);
        Assert.Equal("Shipped", money.Status);
        Assert.Equal(captured, money.Outstanding);
    }

    [Fact]
    public async Task A_shipped_order_can_still_be_refunded_and_keeps_saying_it_shipped()
    {
        // The mirror of the test above, and the reason cancellation refuses rather than the money
        // being unreachable: a parcel in transit is a sale that can be undone in money even though
        // the goods are gone. The status keeps telling the truth about where they are.
        var (shopper, orderNumber, captured, _) = await BuyAsync("Barometer");
        await _shop.ShipAsync(orderNumber);

        using var response = await shopper.RefundAsync(orderNumber, "refund-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var refund = await ResponseReader.RefundAsync(response);
        Assert.Equal("Shipped", refund.Status);
        Assert.Equal(captured, refund.Refunded.Amount);
        Assert.True(refund.FullyRefunded);

        // No restock: the parcel has left, and inventing units here would put goods on the shelf
        // that are in a van.
        Assert.Equal(0, refund.RestockedUnits);
    }

    [Fact]
    public async Task A_refund_larger_than_what_is_left_is_refused_and_records_nothing()
    {
        var (shopper, orderNumber, captured, _) = await BuyAsync("Marlinspike");

        using var response = await shopper.RefundAsync(orderNumber, "refund-1", amount: captured + 1);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Empty(await _shop.RefundsForAsync(orderNumber));
        Assert.Equal(0, (await _shop.MoneyForAsync(orderNumber)).Refunded);
    }

    [Fact]
    public async Task Partial_refunds_accumulate_until_nothing_is_left_to_give_back()
    {
        var (shopper, orderNumber, captured, _) = await BuyAsync("Signal flag");

        using var first = await shopper.RefundAsync(orderNumber, "refund-1", amount: 1_000);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await shopper.RefundAsync(orderNumber, "refund-2", amount: captured - 1_000);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var refund = await ResponseReader.RefundAsync(second);
        Assert.Equal(captured, refund.Refunded.Amount);
        Assert.True(refund.FullyRefunded);
        Assert.Equal(2, refund.Refunds.Count);

        // And the shop stops there.
        using var third = await shopper.RefundAsync(orderNumber, "refund-3", amount: 1);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        Assert.Equal(2, (await _shop.RefundsForAsync(orderNumber)).Count);
    }

    [Fact]
    public async Task An_unpaid_order_has_nothing_to_refund()
    {
        var variant = await _shop.StockAsync("Rope fender", onHand: 1);
        var shopper = await _shop.NewShopperAsync();

        await shopper.AddToCartAsync(variant);

        using var placed = await shopper.CheckoutAsync($"buy-{Guid.CreateVersion7():N}", scenario: "Delay");
        var order = await ResponseReader.OrderAsync(placed);
        Assert.Equal("Pending", order.Status);

        using var response = await shopper.RefundAsync(order.OrderNumber, "refund-1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await ResponseReader.ProblemAsync(response);
        Assert.Contains("cancellation", problem.Detail, StringComparison.Ordinal);

        Assert.Empty(await _shop.RefundsForAsync(order.OrderNumber));
    }

    [Fact]
    public async Task The_retrieval_token_opens_an_order_for_reading_but_not_for_refunding()
    {
        // THE DISTINCTION THIS ENDPOINT IS BUILT ON.
        //
        // The signed token is a bearer capability handed out on the confirmation page so a receipt
        // survives a cleared cookie and can be forwarded to whoever is paying. Reading is what it is
        // for. If it also moved money, forwarding a receipt would hand over the power to refund the
        // order it describes - so refunds require the session that placed the order, and a stranger
        // holding a perfectly valid token gets the same 404 as somebody guessing order numbers.
        var (_, orderNumber, _, token) = await BuyAsync("Sea anchor");

        var stranger = await _shop.NewShopperAsync();

        using var read = await stranger.ReadOrderAsync(orderNumber, token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var refund = await stranger.RefundAsync(orderNumber, "refund-1");
        Assert.Equal(HttpStatusCode.NotFound, refund.StatusCode);

        using var cancel = await stranger.CancelAsync(orderNumber, "cancel-1");
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);

        Assert.Empty(await _shop.RefundsForAsync(orderNumber));
    }

    [Fact]
    public async Task A_refund_without_an_idempotency_key_is_refused()
    {
        var (shopper, orderNumber, _, _) = await BuyAsync("Bosun's whistle");

        using var response = await shopper.Client.PostAsJsonAsync(
            $"/api/orders/{orderNumber}/refunds",
            new { amount = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Empty(await _shop.RefundsForAsync(orderNumber));
    }

    [Fact]
    public async Task A_refunded_order_reports_its_ledger_on_the_receipt()
    {
        // The receipt has to show what went back. One that showed only the capture would be wrong
        // in the direction a shopper notices.
        var (shopper, orderNumber, captured, token) = await BuyAsync("Compass rose");

        using var refunded = await shopper.RefundAsync(orderNumber, "refund-1", amount: 500);
        Assert.Equal(HttpStatusCode.OK, refunded.StatusCode);

        using var receipt = await shopper.ReadOrderAsync(orderNumber, token);
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);

        var view = await receipt.Content.ReadFromJsonAsync<ReceiptView>();
        Assert.NotNull(view);
        Assert.Equal(captured, view.Captured.Amount);
        Assert.Equal(500, view.Refunded.Amount);
        Assert.Equal(captured - 500, view.RefundableRemaining.Amount);

        var entry = Assert.Single(view.Refunds);
        Assert.Equal(500, entry.Amount.Amount);
        Assert.Equal("CustomerRequest", entry.Reason);
    }
}

/// <summary>The order-retrieval response, narrowed to the money and the ledger.</summary>
internal sealed record ReceiptView(
    string OrderNumber,
    string Status,
    MoneyView Captured,
    MoneyView Refunded,
    MoneyView RefundableRemaining,
    IReadOnlyList<RefundEntryView> Refunds);
