using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using VelaCommerce.Api.Endpoints;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Messaging;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Messaging;
using VelaCommerce.Infrastructure.Payments;
using VelaCommerce.Infrastructure.Persistence;

using Xunit;
using Xunit.Sdk;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// A shop with a payment gateway attached, and a way to look at every table the settlement path
/// writes to.
/// <para>
/// It is <see cref="Storefront"/>'s counterpart for the asynchronous half of checkout, and it
/// shares that file's split of responsibilities for the same reason. <strong>Everything a shopper
/// or a gateway does goes over HTTP</strong> into the composed host — adding to a cart, checking
/// out, and posting a signed notification are all requests the real senders make. <strong>
/// Everything a shopkeeper does goes straight to the database</strong> through the fixture's
/// session-less context: seeding stock, cancelling an order, and reading back what happened.
/// Reading an outcome through the same endpoint that produced it would let one bug hide another —
/// a receiver that both mis-applied a settlement and mis-reported it would look correct.
/// </para>
/// <para>
/// The types it reuses from <see cref="Storefront"/> — <see cref="Shopper"/>,
/// <see cref="StockedVariant"/>, <see cref="Ledger"/>, <see cref="ReservationRow"/> and the wire
/// views — are reused deliberately rather than copied: a renamed JSON property should break both
/// suites at once, not one of them.
/// </para>
/// </summary>
internal sealed class SettlementLab : IDisposable
{
    /// <summary>
    /// How long a test will keep sweeping before it gives up on a delivery. Generous against the
    /// 250 ms settlement delay, because what it is really absorbing is a container under load, and
    /// a flaky timeout in a suite about exactly-once delivery is worse than a slow one.
    /// </summary>
    private static readonly TimeSpan DeliveryDeadline = TimeSpan.FromSeconds(20);

    /// <summary>The pause between sweeps, so a not-yet-due message is not polled in a tight loop.</summary>
    private static readonly TimeSpan SweepPause = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Sweeps allowed before a timeline run is declared stuck. Paid to Shipped is two edges and
    /// the worker crosses one per order per sweep, so anything past a handful is a worker that is
    /// not advancing rather than one that is being slow.
    /// </summary>
    private const int MaxTimelineSweeps = 6;

    private readonly PostgresFixture _fixture;
    private readonly HttpClient _gateway;

    public SettlementLab(PostgresFixture fixture)
    {
        _fixture = fixture;
        Host = new SettlementHost(fixture.ConnectionString);
        _gateway = Host.NewGateway();
    }

    /// <summary>The composed API, in-process, with the settlement receiver reachable.</summary>
    public SettlementHost Host { get; }

    public void Dispose()
    {
        _gateway.Dispose();
        Host.Dispose();
    }

    // -------------------------------------------------------------------------------------------
    // Behind the counter.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts a variant in the catalog and a known quantity of it on the shelf.
    /// <para>
    /// Slug and SKU carry a fresh UUID because the whole assembly shares one container, and the
    /// price deliberately does not end in <c>.01</c>–<c>.05</c>: those trailing cents select a
    /// payment scenario in the shipped demo. This host turns that off, but a price that could not
    /// collide anyway costs nothing.
    /// </para>
    /// </summary>
    public async Task<StockedVariant> StockAsync(string name, int onHand = 5, long priceMinorUnits = 4_500)
    {
        await using var db = _fixture.CreateContext();

        var product = new Product(
            $"settle-{Guid.CreateVersion7():N}",
            name,
            "Written by the settlement delivery tests.",
            "checkout");

        var variant = product.AddVariant(
            $"SETL-{Guid.CreateVersion7():N}"[..20],
            "One size",
            new Money(priceMinorUnits));

        db.Products.Add(product);
        db.StockItems.Add(new StockItem(variant.Id, onHand));
        await db.SaveChangesAsync();

        return new StockedVariant(variant.Id, variant.Sku, name, priceMinorUnits);
    }

    /// <summary>The stock ledger as the database holds it, read outside every session.</summary>
    public async Task<Ledger> LedgerAsync(StockedVariant variant)
    {
        await using var db = _fixture.CreateContext();

        var stock = await db.StockItems.AsNoTracking()
            .SingleAsync(item => item.VariantId == variant.VariantId);

        return new Ledger(stock.OnHand, stock.Reserved);
    }

    /// <summary>Every stock reservation raised for a variant, with the status the reaper reads.</summary>
    public async Task<IReadOnlyList<ReservationRow>> ReservationsForAsync(StockedVariant variant)
    {
        await using var db = _fixture.CreateContext();

        return await db.StockReservations.AsNoTracking()
            .Where(reservation => reservation.VariantId == variant.VariantId)
            .Select(reservation => new ReservationRow(
                reservation.OrderId,
                reservation.Quantity,
                reservation.Status.ToString()))
            .ToListAsync();
    }

    /// <summary>
    /// One order exactly as PostgreSQL holds it, including its physical row version.
    /// <para>
    /// The tenancy filter is suppressed by name — and only that one, so soft-deleted rows stay
    /// hidden — because this context has no visitor bound and <c>DemoTenancy</c> fails closed.
    /// Counting zero is the right answer to "what may this caller see" and a useless answer to
    /// "what does the table say", which is the only question a settlement assertion is asking.
    /// </para>
    /// </summary>
    public async Task<OrderSnapshot> OrderAsync(string orderNumber) =>
        await OrderOrNullAsync(orderNumber)
        ?? throw new InvalidOperationException($"No order {orderNumber} exists.");

    /// <summary>The same, for the tests that need to prove an order was never created.</summary>
    public async Task<OrderSnapshot?> OrderOrNullAsync(string orderNumber)
    {
        await using var db = _fixture.CreateContext();

        var order = await db.Orders.AsNoTracking()
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Include(entity => entity.Lines)
            .SingleOrDefaultAsync(entity => entity.OrderNumber == orderNumber);

        if (order is null)
        {
            return null;
        }

        // xmin is PostgreSQL's own answer to "was this row written again?". It is the id of the
        // transaction that last wrote the tuple, so it changes on every UPDATE and on nothing
        // else — which makes it the one assertion a duplicate delivery cannot satisfy by writing
        // the same values a second time. The domain has no version column of its own yet (an
        // xmin-backed concurrency token is planned), so this reads the system column directly.
        var rowVersion = await db.Database
            .SqlQuery<string>($"""SELECT xmin::text AS "Value" FROM orders WHERE order_number = {orderNumber}""")
            .SingleAsync();

        return new OrderSnapshot(
            order.OrderNumber,
            order.Status.ToString(),
            order.Total.Amount,
            order.Captured.Amount,
            order.PaidAt,
            rowVersion);
    }

    /// <summary>
    /// Cancels an order the way the reservation reaper does when a checkout is abandoned: the
    /// aggregate's own transition, not an UPDATE, so the fixture cannot manufacture a state the
    /// application could never have produced.
    /// </summary>
    public async Task CancelAsync(string orderNumber)
    {
        await using var db = _fixture.CreateContext();

        var order = await db.Orders
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .SingleAsync(entity => entity.OrderNumber == orderNumber);

        order.Cancel();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The outbox rows an order's checkout wrote, oldest delivery time first.
    /// <para>
    /// Found by the order number inside the payload rather than by a foreign key, because there is
    /// no foreign key: an outbox message is a promise about a transport and deliberately holds no
    /// reference to the aggregate it is about. Matching on the payload is also a small assertion
    /// in its own right — the reference really is in the bytes that were signed.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<OutboxRow>> OutboxForAsync(string orderNumber)
    {
        await using var db = _fixture.CreateContext();

        return await db.Set<OutboxMessage>().AsNoTracking()
            .Where(message => message.Payload.Contains(orderNumber))
            .OrderBy(message => message.DeliverAfter)
            .ThenBy(message => message.Id)
            .Select(message => new OutboxRow(
                message.Id,
                message.MessageType,
                message.Payload,
                message.SignatureHeader,
                message.Status.ToString(),
                message.Attempts,
                message.LastError,
                message.DeliverAfter,
                message.DeliveredAt))
            .ToListAsync();
    }

    /// <summary>
    /// The processed-event ledger for one order: the rows that make a duplicate delivery a no-op.
    /// </summary>
    public async Task<IReadOnlyList<ProcessedEventRow>> ProcessedForAsync(string orderNumber)
    {
        await using var db = _fixture.CreateContext();

        return await db.Set<ProcessedWebhookEvent>().AsNoTracking()
            .Where(processed => processed.OrderReference == orderNumber)
            .OrderBy(processed => processed.ReceivedAt)
            .Select(processed => new ProcessedEventRow(
                processed.EventId,
                processed.EventType,
                processed.OrderReference,
                processed.ReceivedAt))
            .ToListAsync();
    }

    /// <summary>How many times one gateway event id has been recorded. One, or the dedupe is not one.</summary>
    public async Task<int> ProcessedCountAsync(string eventId)
    {
        await using var db = _fixture.CreateContext();

        return await db.Set<ProcessedWebhookEvent>().AsNoTracking()
            .CountAsync(processed => processed.EventId == eventId);
    }

    // -------------------------------------------------------------------------------------------
    // In front of the counter.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a browser and confirms it starts as a stranger with an empty cart, which is the
    /// precondition every test below would otherwise be assuming.
    /// </summary>
    public async Task<Shopper> NewShopperAsync()
    {
        var client = Host.NewBrowser();

        using var response = await client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return new Shopper(client);
    }

    /// <summary>
    /// Puts one unit of a variant in a new shopper's cart, checks out under
    /// <paramref name="scenario"/>, and returns the order the storefront was shown.
    /// </summary>
    public async Task<OrderView> CheckoutAsync(
        StockedVariant variant,
        string scenario,
        int quantity = 1,
        HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        var shopper = await NewShopperAsync();
        await shopper.AddToCartAsync(variant, quantity);

        using var response = await shopper.CheckoutAsync($"settle-{Guid.CreateVersion7():N}", scenario);

        Assert.Equal(expected, response.StatusCode);

        return await ResponseReader.OrderAsync(response);
    }

    // -------------------------------------------------------------------------------------------
    // The gateway's side of the wire.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Posts one notification to the receiver exactly as the delivery client does: a byte array,
    /// <c>application/json</c>, and the signature header added without validation because its
    /// value contains the comma <c>HttpClient</c> would otherwise treat as a value separator.
    /// </summary>
    /// <param name="payload">The exact bytes. Never an object — the signature covers these octets.</param>
    /// <param name="signatureHeader">
    /// The <c>X-Vela-Signature</c> value, or <see langword="null"/> to send none at all.
    /// </param>
    public async Task<Delivery> DeliverAsync(byte[] payload, string? signatureHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookEndpoints.SettlementRoute)
        {
            Content = new ByteArrayContent(payload),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (signatureHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(PaymentSignature.HeaderName, signatureHeader);
        }

        using var response = await _gateway.SendAsync(request);

        return new Delivery(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Delivers a stored outbox row byte for byte, which is what the dispatcher does.</summary>
    public Task<Delivery> DeliverAsync(OutboxRow message) =>
        DeliverAsync(Encoding.UTF8.GetBytes(message.Payload), message.SignatureHeader);

    /// <summary>
    /// Runs real outbox sweeps until every message for this order has left <c>Pending</c>, and
    /// fails with the table's contents if that never happens.
    /// <para>
    /// A loop rather than a fixed number of sweeps, because the first message is not due for
    /// <see cref="SettlementHost.SettlementDelay"/> and a sweep that runs before then correctly
    /// claims nothing. The sweep itself is the real <see cref="OutboxDispatcher"/> — the same
    /// <c>FOR UPDATE SKIP LOCKED</c> claim, the same delivery client, the same endpoint — with
    /// only its timer replaced.
    /// </para>
    /// </summary>
    public async Task DispatchAsync(string orderNumber)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryDeadline;

        while (true)
        {
            var messages = await OutboxForAsync(orderNumber);

            if (messages.Count > 0 && messages.All(message => message.Status != nameof(OutboxMessageStatus.Pending)))
            {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new XunitException(
                    $"Order {orderNumber} still has undelivered settlement notifications after "
                    + $"{DeliveryDeadline}. The outbox holds:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, messages.Select(message =>
                        $"    {message.MessageType} {message.Status} attempts={message.Attempts} "
                        + $"error={message.LastError ?? "<none>"}")));
            }

            await Host.Dispatcher.SweepAsync(SettlementHost.ReceiverUrl, CancellationToken.None);
            await Task.Delay(SweepPause);
        }
    }

    /// <summary>
    /// Runs the real timeline worker until an order reaches <paramref name="status"/>, one step per
    /// sweep, and fails naming where it actually got to.
    /// <para>
    /// Bounded by a sweep count rather than a clock, because with the dwells collapsed to zero the
    /// only thing between a paid order and a shipped one is the number of sweeps: the worker moves
    /// each order one edge at a time so that a catch-up keeps <c>Packed</c> visible instead of
    /// skipping through it.
    /// </para>
    /// </summary>
    public async Task<OrderTimelineTally> AdvanceTimelineToAsync(string orderNumber, string status)
    {
        var tally = OrderTimelineTally.Empty;

        for (var sweep = 0; sweep < MaxTimelineSweeps; sweep++)
        {
            var current = await OrderAsync(orderNumber);

            if (string.Equals(current.Status, status, StringComparison.Ordinal))
            {
                return tally;
            }

            var result = await Host.Timeline.SweepAsync(CancellationToken.None);
            tally = tally.Add(result.Packed, result.Shipped, result.UnitsShipped);
        }

        var reached = await OrderAsync(orderNumber);

        throw new XunitException(
            $"Order {orderNumber} never reached {status}; {MaxTimelineSweeps} timeline sweeps left it "
            + $"{reached.Status}.");
    }

    // -------------------------------------------------------------------------------------------
    // Signing, as the gateway would.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Serializes and signs an event the way <c>SimulatedPaymentGateway</c> does — the same
    /// serializer options, the same <see cref="PaymentSignature.CreateHeader"/>. Nothing here
    /// re-implements the scheme; a test that did would be asserting against its own bug.
    /// </summary>
    /// <param name="settlement">The event to send.</param>
    /// <param name="signedAt">
    /// The instant bound into the signature. A parameter because it is the whole point of the
    /// replay test: a notification captured ten days ago carries a header that says so.
    /// </param>
    /// <param name="secret">
    /// The signing key. Defaults to the host's; a different one is what forgery looks like.
    /// </param>
    public static (byte[] Payload, string Header) Sign(
        PaymentSettlementEvent settlement,
        DateTimeOffset signedAt,
        string? secret = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(settlement, PaymentSettlementEvent.SerializerOptions);

        return (payload, PaymentSignature.CreateHeader(payload, signedAt, secret ?? SettlementHost.SigningSecret));
    }

    /// <summary>Signs bytes that already exist, for the tests that must not disturb them.</summary>
    public static string HeaderFor(byte[] payload, DateTimeOffset signedAt, string? secret = null) =>
        PaymentSignature.CreateHeader(payload, signedAt, secret ?? SettlementHost.SigningSecret);

    /// <summary>
    /// Reads a stored payload back into an event, using the serializer options the payload was
    /// written with. For reading an event id out of an outbox row — never for re-sending, because
    /// a re-serialization is a different message from the one that was signed.
    /// </summary>
    public static PaymentSettlementEvent EventOf(string payload) =>
        JsonSerializer.Deserialize<PaymentSettlementEvent>(payload, PaymentSettlementEvent.SerializerOptions)
        ?? throw new InvalidOperationException("A stored outbox payload deserialized to null.");

    /// <summary>
    /// A well-formed order number no order in this database can hold.
    /// <para>
    /// Minted from the top of the sequence's range rather than invented as a literal, so it passes
    /// <c>OrderNumbers.TryNormalize</c> — the receiver rejects a malformed reference before it
    /// ever looks in the table, and a test using a made-up string would prove that path instead of
    /// the "no such order" one it means to.
    /// </para>
    /// </summary>
    public static string OrderNumberNobodyHolds() => OrderNumbers.Format(OrderNumbers.MaxSequenceValue);
}

/// <summary>One order as PostgreSQL holds it, with the row version that proves it was not rewritten.</summary>
/// <param name="OrderNumber">The reference a settlement names.</param>
/// <param name="Status">The order state machine's current position.</param>
/// <param name="TotalAmount">What is owed, in minor units.</param>
/// <param name="CapturedAmount">What has been taken, in minor units.</param>
/// <param name="PaidAt">When it was settled, or null.</param>
/// <param name="RowVersion">
/// PostgreSQL's <c>xmin</c> for this row. Equal across two reads means no transaction wrote the
/// row in between — not that it wrote the same values, but that it did not write at all.
/// </param>
internal sealed record OrderSnapshot(
    string OrderNumber,
    string Status,
    long TotalAmount,
    long CapturedAmount,
    DateTimeOffset? PaidAt,
    string RowVersion);

/// <summary>One outbox row, flattened for assertions.</summary>
internal sealed record OutboxRow(
    Guid Id,
    string MessageType,
    string Payload,
    string SignatureHeader,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset DeliverAfter,
    DateTimeOffset? DeliveredAt);

/// <summary>One row of the processed-event ledger: proof a delivery was handled exactly once.</summary>
internal sealed record ProcessedEventRow(
    string EventId,
    string? EventType,
    string? OrderReference,
    DateTimeOffset ReceivedAt);

/// <summary>
/// What the receiver answered, kept as a status and a body rather than a live
/// <see cref="HttpResponseMessage"/> so a test can assert on it after the response is disposed.
/// </summary>
internal sealed record Delivery(HttpStatusCode StatusCode, string Body)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>The acknowledgement on a 200.</summary>
    public AcknowledgementView Acknowledgement() =>
        JsonSerializer.Deserialize<AcknowledgementView>(Body, Wire)
        ?? throw new InvalidOperationException("The webhook receiver answered with a null JSON body.");

    /// <summary>The problem document on a 4xx.</summary>
    public ProblemView Problem() =>
        JsonSerializer.Deserialize<ProblemView>(Body, Wire)
        ?? throw new InvalidOperationException("The webhook receiver answered with a null JSON body.");
}

/// <summary>
/// What the receiver says it did with one delivery.
/// <para>
/// Declared here rather than reusing the API's own contract, for the reason the isolation tests
/// give: a wire-level test that shared the server's type would let a renamed JSON property pass
/// unnoticed on both sides.
/// </para>
/// </summary>
/// <param name="EventId">The gateway's id, echoed back.</param>
/// <param name="Outcome">
/// <c>settled</c>, <c>duplicate</c>, <c>no-legal-transition</c>, <c>order-not-found</c>,
/// <c>acknowledged</c> or <c>unsupported-event-type</c>.
/// </param>
/// <param name="Applied">Whether this delivery is the one that moved the order.</param>
/// <param name="OrderNumber">The order the event named, normalized.</param>
/// <param name="OrderStatus">Where the order stands now, or null when there is no order.</param>
internal sealed record AcknowledgementView(
    string EventId,
    string Outcome,
    bool Applied,
    string? OrderNumber,
    string? OrderStatus);

/// <summary>What a run of timeline sweeps did, accumulated across them.</summary>
/// <param name="Packed">Orders moved Paid to Packed.</param>
/// <param name="Shipped">Orders moved Packed to Shipped.</param>
/// <param name="UnitsShipped">Units the shipments removed from on-hand.</param>
internal readonly record struct OrderTimelineTally(int Packed, int Shipped, int UnitsShipped)
{
    public static OrderTimelineTally Empty => default;

    public OrderTimelineTally Add(int packed, int shipped, int units) =>
        new(Packed + packed, Shipped + shipped, UnitsShipped + units);
}
