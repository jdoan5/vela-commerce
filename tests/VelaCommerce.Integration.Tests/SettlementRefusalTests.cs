using System.Globalization;
using System.Net;
using System.Text;

using VelaCommerce.Domain.Common;
using VelaCommerce.Infrastructure.Payments;

using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// What the settlement receiver does with everything that is not a well-timed, well-signed
/// notification for an order that can legally be paid: settlements that arrive too late in the
/// order's life, settlements for orders that do not exist, forgeries, and replays.
///
/// <para>
/// <b>The two failure modes these guard against are opposites, and both are silent.</b> A
/// receiver that refuses too little marks orders paid on the strength of bytes anybody could
/// have written. A receiver that refuses too loudly answers 4xx or 5xx to a delivery nobody can
/// fix — an order the reaper cancelled while the settlement was in flight, an order a demo reset
/// wiped — and the dispatcher then burns its five attempts and abandons a message with an
/// alarming last error, on every reset, for something nobody did wrong. So each test below
/// asserts on the status code <em>and</em> on the rows, because the two halves of "handled
/// correctly" are "the order did not change" and "the sender was told the right thing to do".
/// </para>
///
/// <para>
/// <b>Arrival order is never trusted, and nothing here depends on it.</b> Correctness comes from
/// <c>OrderStateMachine</c>, which has no <c>Paid -&gt; Paid</c> edge and no backwards edge, so a
/// replay, a late authorization and a settlement for a cancelled order are all refused by
/// construction rather than by a status check somebody remembered to write. The
/// <see cref="A_late_authorization_cannot_undo_a_capture_that_already_landed"/> case is the one
/// that would pass by luck under a receiver that sorted by <c>Sequence</c> or by
/// <c>OccurredAt</c>; it is here because a real provider promises neither.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SettlementRefusalTests : IDisposable
{
    /// <summary>
    /// A key that is the right shape and the wrong value. Long enough to pass
    /// <c>PaymentSimulatorOptions.Validate</c>, so what this exercises is the MAC comparison and
    /// not a configuration guard.
    /// </summary>
    private const string SomebodyElsesKey = "not-the-vela-signing-key-but-exactly-as-long-0123456789abcdef01";

    private readonly SettlementLab _lab;

    public SettlementRefusalTests(PostgresFixture fixture) => _lab = new SettlementLab(fixture);

    public void Dispose() => _lab.Dispose();

    // ------------------------------------------------------------------ arriving out of order

    /// <summary>
    /// A settlement for an order that has already been cancelled is recorded, dropped, and
    /// answered 200 — it does not resurrect the order and it does not 500.
    /// <para>
    /// This is the race the reservation reaper creates: it cancels orders still <c>Pending</c>
    /// once their reservation window closes, and a settlement can be in flight at that moment. The
    /// state machine settles it — there is no <c>Cancelled -&gt; Paid</c> edge — and the receiver
    /// asks it rather than letting <c>Order.MarkPaid</c> throw, so that this stays distinguishable
    /// from the genuinely alarming case where the money disagrees with the total.
    /// </para>
    /// <para>
    /// 200 rather than 4xx because the sender did nothing wrong and retrying cannot help. The
    /// dedupe row is still written: this delivery <em>was</em> handled, and the handling was to
    /// drop it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settlement_for_a_cancelled_order_is_recorded_and_dropped_rather_than_paid()
    {
        var oar = await _lab.StockAsync("Spruce oar", onHand: 4);
        var order = await _lab.CheckoutAsync(oar, scenario: "Delay");

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var settlement = SettlementLab.EventOf(notification.Payload);

        // The reaper gets there first: the window closed while the settlement was in flight.
        await _lab.CancelAsync(order.OrderNumber);

        var cancelled = await _lab.OrderAsync(order.OrderNumber);
        Assert.Equal("Cancelled", cancelled.Status);

        var delivery = await _lab.DeliverAsync(notification);
        var acknowledgement = delivery.Acknowledgement();

        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
        Assert.Equal("no-legal-transition", acknowledgement.Outcome);
        Assert.False(acknowledgement.Applied);
        Assert.Equal(order.OrderNumber, acknowledgement.OrderNumber);

        // The sender is told where the order actually stands, which is what makes the 200
        // actionable rather than merely quiet.
        Assert.Equal("Cancelled", acknowledgement.OrderStatus);

        // Nothing was captured and the row was not written again — same snapshot, same xmin.
        Assert.Equal(cancelled, await _lab.OrderAsync(order.OrderNumber));
        Assert.Equal(0, cancelled.CapturedAmount);
        Assert.Null(cancelled.PaidAt);

        // Recorded, so the inevitable redelivery costs one failed insert instead of another
        // lookup and another decision.
        Assert.Equal(1, await _lab.ProcessedCountAsync(settlement.EventId));
    }

    /// <summary>
    /// A settlement naming an order this shop never issued is acknowledged and dropped, not 404'd.
    /// <para>
    /// A 404 tells the sender "retry, it may show up". Here it never will: the notification is
    /// enqueued by the same <c>SaveChangesAsync</c> that commits the order, so the
    /// arrival-before-insert race that makes 404 right for a real gateway is structurally
    /// impossible in this system. What is left as a cause is a demo reset wiping orders while
    /// notifications are in flight, and for that a 404 costs five retries and an abandoned message
    /// every time.
    /// </para>
    /// <para>
    /// The order number is minted from the top of the sequence's range rather than invented, so it
    /// passes <c>OrderNumbers.TryNormalize</c> and reaches the lookup. A made-up string would be
    /// refused as malformed before the database was touched, which is a different code path and
    /// not the one this test is about.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settlement_for_an_order_this_shop_never_issued_is_acknowledged_and_dropped()
    {
        var ghost = SettlementLab.OrderNumberNobodyHolds();

        var settlement = new PaymentSettlementEvent
        {
            EventId = $"evt-ghost-{Guid.CreateVersion7():N}",
            EventType = PaymentSettlementEvent.SucceededType,
            GatewayReference = "sim-ghost",
            OrderReference = ghost,
            SettlementCorrelationId = "sim-ghost-settlement",
            Amount = 4_500L,
            Currency = Money.DefaultCurrency,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        var (payload, header) = SettlementLab.Sign(settlement, DateTimeOffset.UtcNow);

        var delivery = await _lab.DeliverAsync(payload, header);
        var acknowledgement = delivery.Acknowledgement();

        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
        Assert.Equal("order-not-found", acknowledgement.Outcome);
        Assert.False(acknowledgement.Applied);
        Assert.Equal(ghost, acknowledgement.OrderNumber);
        Assert.Null(acknowledgement.OrderStatus);

        // No order was conjured out of the notification, and the delivery was recorded as handled.
        Assert.Null(await _lab.OrderOrNullAsync(ghost));
        Assert.Equal(1, await _lab.ProcessedCountAsync(settlement.EventId));
    }

    /// <summary>
    /// The <c>Reorder</c> scenario: the capture is delivered before the authorization that
    /// logically preceded it, and the late authorization does not undo the payment.
    /// <para>
    /// The gateway raises <c>payment.authorized</c> first and <c>payment.succeeded</c> second, and
    /// delivers them the other way round — which is what a real network does and what a receiver
    /// that trusted arrival order would get wrong. Two things make the outcome correct regardless:
    /// the state machine has no edge that walks an order backwards, and <c>payment.authorized</c>
    /// moves nothing in the first place because funds reserved are not funds moved.
    /// </para>
    /// <para>
    /// Both events are recorded, because they are genuinely two different events with two
    /// different ids — deduplication must not collapse them. The order's row version is unchanged
    /// across the late arrival, which is the assertion that separates "correctly did nothing" from
    /// "wrote Paid over Paid".
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_late_authorization_cannot_undo_a_capture_that_already_landed()
    {
        var log = await _lab.StockAsync("Taffrail log", onHand: 3);
        var order = await _lab.CheckoutAsync(log, scenario: "Reorder");

        var enqueued = await _lab.OutboxForAsync(order.OrderNumber);

        Assert.Equal(2, enqueued.Count);

        // Delivered capture-first, raised authorization-first. The sequence numbers are inverted
        // relative to the delivery schedule, which is the whole scenario.
        Assert.Equal(PaymentSettlementEvent.SucceededType, enqueued[0].MessageType);
        Assert.Equal(PaymentSettlementEvent.AuthorizedType, enqueued[1].MessageType);
        Assert.Equal(2, SettlementLab.EventOf(enqueued[0].Payload).Sequence);
        Assert.Equal(1, SettlementLab.EventOf(enqueued[1].Payload).Sequence);

        var capture = await _lab.DeliverAsync(enqueued[0]);

        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        Assert.Equal("settled", capture.Acknowledgement().Outcome);

        var paid = await _lab.OrderAsync(order.OrderNumber);

        Assert.Equal("Paid", paid.Status);
        Assert.Equal(order.Total.Amount, paid.CapturedAmount);

        // And now the event that was raised first arrives last.
        var late = await _lab.DeliverAsync(enqueued[1]);
        var acknowledgement = late.Acknowledgement();

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal("acknowledged", acknowledgement.Outcome);
        Assert.False(acknowledgement.Applied);
        Assert.Equal("Paid", acknowledgement.OrderStatus);

        Assert.Equal(paid, await _lab.OrderAsync(order.OrderNumber));

        // Two distinct events, two distinct rows: deduplication is on the gateway's event id, not
        // on the order or on the content.
        var recorded = await _lab.ProcessedForAsync(order.OrderNumber);

        Assert.Equal(2, recorded.Count);
        Assert.Equal(2, recorded.Select(row => row.EventId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(recorded, row => row.EventType == PaymentSettlementEvent.SucceededType);
        Assert.Contains(recorded, row => row.EventType == PaymentSettlementEvent.AuthorizedType);
    }

    // ------------------------------------------------------------------ forged and replayed

    /// <summary>
    /// A payload edited in flight is refused, and the order keeps the price it was placed at.
    /// <para>
    /// The tamper is the one an attacker would actually attempt: multiply the captured amount by
    /// ten and keep the gateway's own signature, which is exactly what a receiver that verified a
    /// re-serialization of its bound DTO — rather than the transmitted octets — would wave
    /// through. If verification ever moved after model binding, this test is the one that says so.
    /// </para>
    /// <para>
    /// The refusal must also say nothing. The problem document is checked for the order number,
    /// because an endpoint anybody can reach that confirms which references exist is an oracle,
    /// and the whole point of answering every unverified request identically is that probing it
    /// reveals only what this public repository already documents.
    /// </para>
    /// <para>
    /// It ends by delivering the untouched notification successfully. Without that, a receiver
    /// that refused everything would pass — and "refuses forgeries" would be indistinguishable
    /// from "is broken".
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tampered_payload_is_refused_and_the_order_keeps_its_price()
    {
        var chart = await _lab.StockAsync("Admiralty chart", onHand: 2);
        var order = await _lab.CheckoutAsync(chart, scenario: "Delay");

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var settlement = SettlementLab.EventOf(notification.Payload);
        var before = await _lab.OrderAsync(order.OrderNumber);

        var honest = order.Total.Amount.ToString(CultureInfo.InvariantCulture);
        var inflated = (order.Total.Amount * 10L).ToString(CultureInfo.InvariantCulture);

        var tampered = notification.Payload.Replace(
            $"\"amount\":{honest}",
            $"\"amount\":{inflated}",
            StringComparison.Ordinal);

        // The edit landed. Without this the test could pass by sending the original bytes.
        Assert.NotEqual(notification.Payload, tampered);

        var refused = await _lab.DeliverAsync(
            Encoding.UTF8.GetBytes(tampered),
            notification.SignatureHeader);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var problem = refused.Problem();

        Assert.DoesNotContain(order.OrderNumber, problem.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(order.OrderNumber, problem.Title ?? string.Empty, StringComparison.Ordinal);

        // Nothing recorded, nothing applied, the row not written.
        Assert.Equal(before, await _lab.OrderAsync(order.OrderNumber));
        Assert.Equal("Pending", before.Status);
        Assert.Equal(0, before.CapturedAmount);
        Assert.Equal(0, await _lab.ProcessedCountAsync(settlement.EventId));
        Assert.Empty(await _lab.ProcessedForAsync(order.OrderNumber));

        // The control: the same order, the same receiver, the untransformed notification.
        var honestDelivery = await _lab.DeliverAsync(notification);

        Assert.Equal(HttpStatusCode.OK, honestDelivery.StatusCode);
        Assert.Equal("settled", honestDelivery.Acknowledgement().Outcome);
        Assert.Equal("Paid", (await _lab.OrderAsync(order.OrderNumber)).Status);
    }

    /// <summary>
    /// A perfectly well-formed notification signed with a key that is not ours is refused, and
    /// nothing about the order changes.
    /// <para>
    /// The complement of the tamper above: there, authentic bytes were edited under an authentic
    /// signature; here the bytes are exactly what the gateway would send and only the key is
    /// wrong. Both must fail, and both must fail the same way, because a receiver that
    /// distinguished them in its answer would be telling an attacker which half to keep working
    /// on. The 401 carries a challenge because RFC 9110 §15.5.2 requires one of any 401.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_notification_signed_with_the_wrong_key_is_refused()
    {
        var glass = await _lab.StockAsync("Hourglass", onHand: 2);
        var order = await _lab.CheckoutAsync(glass, scenario: "Delay");

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var settlement = SettlementLab.EventOf(notification.Payload);
        var before = await _lab.OrderAsync(order.OrderNumber);

        var payload = Encoding.UTF8.GetBytes(notification.Payload);
        var forged = SettlementLab.HeaderFor(payload, DateTimeOffset.UtcNow, SomebodyElsesKey);

        var refused = await _lab.DeliverAsync(payload, forged);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(before, await _lab.OrderAsync(order.OrderNumber));
        Assert.Equal(0, await _lab.ProcessedCountAsync(settlement.EventId));
    }

    /// <summary>
    /// A notification with no signature at all is refused before anything is read into the domain.
    /// <para>
    /// The cheapest forgery, and the one a misconfigured sender produces by accident. It is
    /// answered 400 rather than 401 because there is no credential to challenge — but with the
    /// same title and the same detail as every other refusal, so the status code is a retry
    /// instruction and not a hint about which check failed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unsigned_notification_is_refused()
    {
        var line = await _lab.StockAsync("Lead line", onHand: 2);
        var order = await _lab.CheckoutAsync(line, scenario: "Delay");

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var before = await _lab.OrderAsync(order.OrderNumber);

        var refused = await _lab.DeliverAsync(Encoding.UTF8.GetBytes(notification.Payload), signatureHeader: null);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(before, await _lab.OrderAsync(order.OrderNumber));
        Assert.Empty(await _lab.ProcessedForAsync(order.OrderNumber));
    }

    /// <summary>
    /// A settlement signed ten days ago cannot be replayed today, and one signed ten days from now
    /// is refused too.
    /// <para>
    /// This is what the timestamp inside the MAC buys. The signed message is
    /// <c>{unix-seconds}.{body}</c>, so a signature lifted from a log or a proxy cannot be
    /// re-dated: changing <c>t</c> invalidates the hash, and leaving it alone puts the request
    /// outside the tolerance. Both directions are checked — a timestamp far in the future is skew
    /// or forgery, not freshness, and a one-sided window would accept a forged
    /// <c>t</c> of the year 2100 forever.
    /// </para>
    /// <para>
    /// The header is built by signing the genuine payload at an old instant, which is byte for
    /// byte what the gateway would have emitted then; an attacker holding a captured request holds
    /// exactly this. The fresh delivery at the end proves the payload was never the problem.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settlement_captured_today_cannot_be_replayed_next_week()
    {
        var chronometer = await _lab.StockAsync("Marine chronometer", onHand: 2);
        var order = await _lab.CheckoutAsync(chronometer, scenario: "Delay");

        var notification = Assert.Single(await _lab.OutboxForAsync(order.OrderNumber));
        var settlement = SettlementLab.EventOf(notification.Payload);
        var before = await _lab.OrderAsync(order.OrderNumber);

        var payload = Encoding.UTF8.GetBytes(notification.Payload);
        var now = DateTimeOffset.UtcNow;

        var stale = await _lab.DeliverAsync(payload, SettlementLab.HeaderFor(payload, now.AddDays(-10)));

        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var skewed = await _lab.DeliverAsync(payload, SettlementLab.HeaderFor(payload, now.AddDays(10)));

        Assert.Equal(HttpStatusCode.BadRequest, skewed.StatusCode);

        Assert.Equal(before, await _lab.OrderAsync(order.OrderNumber));
        Assert.Equal(0, await _lab.ProcessedCountAsync(settlement.EventId));

        // The same bytes, in date: accepted. The refusals above were about the clock and nothing
        // else, which is the claim this test is really making.
        var fresh = await _lab.DeliverAsync(notification);

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal("settled", fresh.Acknowledgement().Outcome);
        Assert.Equal(1, await _lab.ProcessedCountAsync(settlement.EventId));
    }
}
