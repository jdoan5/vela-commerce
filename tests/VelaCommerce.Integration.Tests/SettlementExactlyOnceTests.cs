using System.Net;

using VelaCommerce.Infrastructure.Payments;

using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence behind the claim that a shopper is charged once however many times the gateway
/// tells us they paid.
///
/// <para>
/// At-least-once is not a defect anybody chose. A deliverer that crashes between "the receiver
/// answered 200" and "the row says Delivered" has no honest option but to send again, and every
/// real gateway makes the same promise for the same reason. So exactly-once <em>effect</em> is
/// built at the receiving end out of two at-least-once halves: the event id is inserted and the
/// order transition applied in one transaction, and the second delivery loses on
/// <c>pk_processed_webhook_events</c>, taking the transition down with it.
/// </para>
///
/// <para>
/// <b>Why every assertion here reads the database rather than the status code.</b> A receiver
/// that answered 200 twice and paid twice would pass a status-code test perfectly. So would one
/// that answered 200 twice and paid nothing. The claim is about rows: one processed-event row,
/// one captured amount equal to the total, and — the assertion that no amount of re-writing the
/// same values can satisfy — an order row whose PostgreSQL <c>xmin</c> is unchanged across the
/// duplicate, which means no transaction wrote it at all.
/// </para>
///
/// <para>
/// <b>These tests were confirmed to fail against a broken implementation.</b> Commenting out the
/// <c>ProcessedWebhookEvent</c> insert in <c>WebhookEndpoints.ApplySettlementAsync</c> — leaving
/// everything else, including the state machine — turns
/// <see cref="A_settlement_delivered_twice_pays_the_order_once"/> and
/// <see cref="Two_simultaneous_copies_of_one_settlement_move_the_order_once"/> red. That check
/// matters more than the tests passing: a suite that goes green against a receiver with no dedupe
/// at all would be worse than no suite, because it would say the opposite of the truth.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SettlementExactlyOnceTests : IDisposable
{
    private readonly SettlementLab _lab;

    public SettlementExactlyOnceTests(PostgresFixture fixture) => _lab = new SettlementLab(fixture);

    /// <summary>Disposes the host, its clients, the dispatcher's HTTP handler and the key ring.</summary>
    public void Dispose() => _lab.Dispose();

    /// <summary>
    /// The same signed notification delivered twice: the order is paid once, the second delivery
    /// is answered 200, and the order row is not written again.
    /// <para>
    /// The bytes are the gateway's own — read back out of the outbox row checkout wrote and posted
    /// verbatim — so this is a genuine redelivery rather than a second event that happens to say
    /// the same thing. A receiver deduping on content would pass a test built the other way and
    /// fail in production the first time a gateway retried.
    /// </para>
    /// <para>
    /// The last assertion is the one with teeth. <c>xmin</c> is the id of the transaction that
    /// last wrote the tuple, so comparing the whole snapshot — status, captured amount, paid-at
    /// <em>and</em> row version — before and after the duplicate distinguishes "nothing changed"
    /// from "the same values were written a second time". Only the first is exactly-once.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settlement_delivered_twice_pays_the_order_once()
    {
        var lantern = await _lab.StockAsync("Anchor lantern", onHand: 3);
        var order = await _lab.CheckoutAsync(lantern, scenario: "Delay");

        Assert.Equal("Pending", order.Status);
        Assert.NotNull(order.Payment);
        Assert.True(order.Payment.AwaitsSettlement);

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var settlement = SettlementLab.EventOf(notification.Payload);

        Assert.Equal(PaymentSettlementEvent.SucceededType, notification.MessageType);

        var first = await _lab.DeliverAsync(notification);
        var applied = first.Acknowledgement();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("settled", applied.Outcome);
        Assert.True(applied.Applied);

        var afterFirst = await _lab.OrderAsync(order.OrderNumber);

        Assert.Equal("Paid", afterFirst.Status);
        Assert.Equal(order.Total.Amount, afterFirst.CapturedAmount);
        Assert.Equal(afterFirst.TotalAmount, afterFirst.CapturedAmount);
        Assert.NotNull(afterFirst.PaidAt);

        // The gateway sends it again, byte for byte, signature and all.
        var second = await _lab.DeliverAsync(notification);
        var acknowledgement = second.Acknowledgement();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("duplicate", acknowledgement.Outcome);
        Assert.False(acknowledgement.Applied);
        Assert.Equal(settlement.EventId, acknowledgement.EventId);

        // A duplicate is told what the order already is, which is what turns "duplicate" into
        // something a sender can act on rather than merely stop retrying about.
        Assert.Equal(order.OrderNumber, acknowledgement.OrderNumber);
        Assert.Equal("Paid", acknowledgement.OrderStatus);

        Assert.Equal(afterFirst, await _lab.OrderAsync(order.OrderNumber));
        Assert.Equal(1, await _lab.ProcessedCountAsync(settlement.EventId));
        Assert.Single(await _lab.ProcessedForAsync(order.OrderNumber));

        // Paying does not move stock; shipping does. The units stay reserved and the reservation
        // is Confirmed, which is what stops the reaper handing them back to the pool.
        Assert.Equal(new Ledger(3, 1), await _lab.LedgerAsync(lantern));
        Assert.Equal("Confirmed", Assert.Single(await _lab.ReservationsForAsync(lantern)).Status);
    }

    /// <summary>
    /// Two copies of one settlement delivered at the same instant: one transition, no 500, no
    /// second capture.
    /// <para>
    /// <b>This is the test the primary key exists for.</b> The sequential case above passes
    /// against a receiver that merely asks "have I seen this event?" before applying it, because
    /// by the time the second delivery arrives the first has committed. Two deliveries genuinely
    /// in flight together both find nothing, both decide to apply, and both proceed — which is why
    /// the receiver has no such query and lets <c>pk_processed_webhook_events</c> pick the winner
    /// inside the same transaction as the transition.
    /// </para>
    /// <para>
    /// Both requests are parked on one gate and released together, for the reason
    /// <see cref="CheckoutStockRaceTests"/> gives: started in a loop, the first would usually be
    /// finished before the second was written, and the test would prove only that a receiver can
    /// handle one thing at a time.
    /// </para>
    /// <para>
    /// The status-code assertion is not decoration. A lost race here surfacing as a 500 would be
    /// retried by the dispatcher, five times, and then abandoned — a payment silently not applied
    /// because two copies of it arrived too close together.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_copies_of_one_settlement_move_the_order_once()
    {
        var sextant = await _lab.StockAsync("Bronze sextant", onHand: 2);
        var order = await _lab.CheckoutAsync(sextant, scenario: "Delay");

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var settlement = SettlementLab.EventOf(notification.Payload);

        var deliveries = await Storefront.AllAtOnceAsync(2, _ => _lab.DeliverAsync(notification));

        Assert.All(deliveries, delivery => Assert.Equal(HttpStatusCode.OK, delivery.StatusCode));

        var acknowledgements = deliveries.Select(delivery => delivery.Acknowledgement()).ToList();

        // Exactly one delivery claims to have moved the order, whichever of the two won.
        Assert.Equal("settled", Assert.Single(acknowledgements, ack => ack.Applied).Outcome);
        Assert.Equal("duplicate", Assert.Single(acknowledgements, ack => !ack.Applied).Outcome);
        Assert.All(acknowledgements, ack => Assert.Equal(settlement.EventId, ack.EventId));

        Assert.Equal(1, await _lab.ProcessedCountAsync(settlement.EventId));

        var settled = await _lab.OrderAsync(order.OrderNumber);

        Assert.Equal("Paid", settled.Status);
        Assert.Equal(order.Total.Amount, settled.CapturedAmount);
        Assert.Equal(settled.TotalAmount, settled.CapturedAmount);

        // One reservation, confirmed once. A second application would have tried to confirm an
        // already-Confirmed reservation, which the domain refuses outright.
        Assert.Equal("Confirmed", Assert.Single(await _lab.ReservationsForAsync(sextant)).Status);
        Assert.Equal(new Ledger(2, 1), await _lab.LedgerAsync(sextant));
    }

    /// <summary>
    /// The <c>Duplicate</c> scenario end to end: the simulator plans two deliveries of one event,
    /// the outbox makes both of them, and the shop takes the money once.
    /// <para>
    /// The same property as the first test, reached through the machinery that will actually
    /// produce it in the demo rather than by hand. The check that the two rows carry identical
    /// payloads and identical signatures is what makes this a duplicate-delivery test at all: two
    /// distinct events that happened to agree would exercise nothing.
    /// </para>
    /// <para>
    /// Both outbox rows must finish <c>Delivered</c>, and that is the point of answering a
    /// duplicate with 200 rather than a 409. A non-2xx would be retried five times and abandoned
    /// with an alarming last error, on every single duplicate, for a delivery that was handled
    /// perfectly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_duplicate_scenario_delivers_one_event_twice_and_the_shop_charges_once()
    {
        var kettle = await _lab.StockAsync("Brass kettle", onHand: 4);
        var order = await _lab.CheckoutAsync(kettle, scenario: "Duplicate");

        var enqueued = await _lab.OutboxForAsync(order.OrderNumber);

        Assert.Equal(2, enqueued.Count);
        Assert.Equal(enqueued[0].Payload, enqueued[1].Payload);
        Assert.Equal(enqueued[0].SignatureHeader, enqueued[1].SignatureHeader);
        Assert.NotEqual(enqueued[0].Id, enqueued[1].Id);
        Assert.All(enqueued, message => Assert.Equal("Pending", message.Status));

        await _lab.DispatchAsync(order.OrderNumber);

        var delivered = await _lab.OutboxForAsync(order.OrderNumber);

        Assert.Equal(2, delivered.Count);
        Assert.All(delivered, message =>
        {
            Assert.Equal("Delivered", message.Status);
            Assert.Equal(1, message.Attempts);
            Assert.Null(message.LastError);
        });

        var settled = await _lab.OrderAsync(order.OrderNumber);

        Assert.Equal("Paid", settled.Status);
        Assert.Equal(order.Total.Amount, settled.CapturedAmount);

        var recorded = Assert.Single(await _lab.ProcessedForAsync(order.OrderNumber));

        Assert.Equal(SettlementLab.EventOf(delivered[0].Payload).EventId, recorded.EventId);
        Assert.Equal(1, await _lab.ProcessedCountAsync(recorded.EventId));

        Assert.Equal(new Ledger(4, 1), await _lab.LedgerAsync(kettle));
    }
}
