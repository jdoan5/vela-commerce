using System.Text;

using VelaCommerce.Infrastructure.Payments;

using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The whole asynchronous loop, driven end to end: a shopper checks out under a scenario the
/// gateway defers, checkout commits a promise instead of a payment, the outbox keeps the promise
/// over real HTTP, the receiver applies it, and the fulfilment timeline carries the paid order to
/// the door and takes the units off the shelf.
///
/// <para>
/// <b>This is the test most likely to be broken by a byte-for-byte payload bug, which is why it
/// exists in this shape.</b> Every other test in this suite hands the receiver bytes read straight
/// out of the outbox table. Only this one lets the real <c>OutboxDispatcher</c> read the row,
/// encode it, attach the stored header and post it — the four steps where a payload can be
/// deserialized, re-serialized and re-signed into something that no longer matches its MAC. A
/// receiver reporting a signature mismatch for that reason looks exactly like an attack and is
/// not one, and nothing further down the chain could tell the difference.
/// </para>
///
/// <para>
/// <b>Both background workers are driven by hand, and their timers are off.</b> The fixture's
/// PostgreSQL container is shared by the whole assembly, so a dispatcher polling on its own would
/// pick up outbox rows written by other test classes — signed with a different host's secret — and
/// retry them until they were abandoned. What is under test is the sweep, not the timer:
/// <c>SweepAsync</c> is production code on both workers and is public precisely so a test can
/// drive it without waiting.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SettlementLifecycleTests : IDisposable
{
    private readonly SettlementLab _lab;

    public SettlementLifecycleTests(PostgresFixture fixture) => _lab = new SettlementLab(fixture);

    public void Dispose() => _lab.Dispose();

    /// <summary>
    /// A deferred payment: the shopper is told "confirming payment", the outbox delivers the
    /// settlement a moment later, and the order reaches Paid on its own.
    /// <para>
    /// The order of the assertions follows the order of the promises. Checkout answers 202 with a
    /// Pending order and nothing captured, which is the honest thing to show a shopper whose money
    /// has not moved. The notification is committed in the <em>same</em> transaction as the order —
    /// so it cannot be delivered before the order it names is visible — and it is not yet due,
    /// which is what makes this an asynchronous path rather than a slow synchronous one. Then a
    /// sweep, and the money.
    /// </para>
    /// <para>
    /// The stored bytes are put through the receiver's own verifier before anything is delivered.
    /// That check is not redundant with the delivery below: it distinguishes "the enqueue path
    /// stored a payload that no longer matches its signature" from "the dispatcher changed the
    /// bytes on the way out", which would otherwise arrive as the same 401 with no way to tell
    /// which half is at fault.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_deferred_payment_is_confirmed_by_the_outbox_without_anyone_asking_again()
    {
        var compass = await _lab.StockAsync("Binnacle compass", onHand: 6, priceMinorUnits: 12_750);
        var order = await _lab.CheckoutAsync(compass, scenario: "Delay", quantity: 2);

        // What the shopper is shown: nothing taken yet, and the reason said out loud.
        Assert.Equal("Pending", order.Status);
        Assert.Equal(0, order.Captured.Amount);
        Assert.NotNull(order.Payment);
        Assert.True(order.Payment.AwaitsSettlement);
        Assert.False(order.Payment.Captured);

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));

        Assert.Equal(PaymentSettlementEvent.SucceededType, notification.MessageType);
        Assert.Equal("Pending", notification.Status);
        Assert.Equal(0, notification.Attempts);
        Assert.Null(notification.DeliveredAt);

        // The row was written by checkout's own transaction, and the payload it holds still
        // verifies against the header beside it.
        Assert.Equal(
            PaymentSignatureResult.Valid,
            PaymentSignature.Verify(
                Encoding.UTF8.GetBytes(notification.Payload),
                notification.SignatureHeader,
                SettlementHost.SigningSecret,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(5)));

        // Stock is spoken for but nothing is promised, so the reservation is still Held and the
        // reaper would release it if the settlement never came.
        Assert.Equal("Held", Assert.Single(await _lab.ReservationsForAsync(compass)).Status);
        Assert.Equal(new Ledger(6, 2), await _lab.LedgerAsync(compass));

        await _lab.DispatchAsync(order.OrderNumber);

        var delivered = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));

        Assert.Equal("Delivered", delivered.Status);
        Assert.Equal(1, delivered.Attempts);
        Assert.Null(delivered.LastError);
        Assert.NotNull(delivered.DeliveredAt);

        var settled = await _lab.OrderAsync(order.OrderNumber);

        Assert.Equal("Paid", settled.Status);
        Assert.Equal(order.Total.Amount, settled.CapturedAmount);
        Assert.Equal(settled.TotalAmount, settled.CapturedAmount);
        Assert.NotNull(settled.PaidAt);

        // The receiver read the bytes the dispatcher sent, not a reconstruction of them: the id it
        // recorded is the id inside the stored payload.
        var recorded = Assert.Single(await _lab.ProcessedForAsync(order.OrderNumber));

        Assert.Equal(SettlementLab.EventOf(delivered.Payload).EventId, recorded.EventId);
        Assert.Equal(PaymentSettlementEvent.SucceededType, recorded.EventType);

        // Confirming the reservation inside the settle transaction is what stops the reaper
        // releasing units fifteen minutes later while the order stays Paid — an oversell with no
        // error anywhere.
        Assert.Equal("Confirmed", Assert.Single(await _lab.ReservationsForAsync(compass)).Status);

        // Payment does not move stock. On-hand drops when the parcel ships, and not before.
        Assert.Equal(new Ledger(6, 2), await _lab.LedgerAsync(compass));
    }

    /// <summary>
    /// A paid order is packed, then shipped, and shipping is what actually takes the units off the
    /// shelf.
    /// <para>
    /// It starts from an order the <em>webhook</em> paid rather than one checkout captured
    /// synchronously, because that is the shape with the sharp edge: those reservations were left
    /// <c>Held</c> by checkout and confirmed by the receiver, and if either half had skipped that
    /// step the shipment below would find nothing to deduct and the ledger would end up
    /// overstated with no error raised anywhere.
    /// </para>
    /// <para>
    /// Packing is asserted to move no stock at all, which is the distinction the two steps exist
    /// to make: <c>reserved</c> is a promise and <c>on_hand</c> is a shelf, and only one of them
    /// changes when a parcel leaves. The final sweep is the terminal check — <c>Shipped</c> has no
    /// outgoing edge and no self-transition, so a worker that ran twice against a shipped order
    /// must move nothing rather than deduct the units a second time.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_paid_order_is_packed_then_shipped_and_shipping_takes_the_units_off_the_shelf()
    {
        const int OnHand = 5;
        const int Ordered = 2;

        var anchor = await _lab.StockAsync("Kedge anchor", onHand: OnHand);
        var order = await _lab.CheckoutAsync(anchor, scenario: "Delay", quantity: Ordered);

        await _lab.DispatchAsync(order.OrderNumber);

        Assert.Equal("Paid", (await _lab.OrderAsync(order.OrderNumber)).Status);
        Assert.Equal(new Ledger(OnHand, Ordered), await _lab.LedgerAsync(anchor));

        await _lab.AdvanceTimelineToAsync(order.OrderNumber, "Packed");

        Assert.Equal("Packed", (await _lab.OrderAsync(order.OrderNumber)).Status);

        // Packing is a promise about a box, not a movement of stock.
        Assert.Equal(new Ledger(OnHand, Ordered), await _lab.LedgerAsync(anchor));

        await _lab.AdvanceTimelineToAsync(order.OrderNumber, "Shipped");

        var shipped = await _lab.OrderAsync(order.OrderNumber);

        Assert.Equal("Shipped", shipped.Status);

        // The units have physically left: on-hand drops by the quantity and the reservation that
        // was holding them drops with it.
        Assert.Equal(new Ledger(OnHand - Ordered, 0), await _lab.LedgerAsync(anchor));
        Assert.Equal("Confirmed", Assert.Single(await _lab.ReservationsForAsync(anchor)).Status);

        // The money is untouched by fulfilment; a shipped order is still paid exactly once.
        Assert.Equal(order.Total.Amount, shipped.CapturedAmount);

        // Shipped is terminal. Another sweep must be a no-op, not a second shipment.
        await _lab.Host.Timeline.SweepAsync(CancellationToken.None);

        Assert.Equal(new Ledger(OnHand - Ordered, 0), await _lab.LedgerAsync(anchor));
        Assert.Equal(shipped, await _lab.OrderAsync(order.OrderNumber));
    }
}
