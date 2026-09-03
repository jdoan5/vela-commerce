using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Infrastructure.Persistence;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// A shop with stock in it, some visitors, and a way to look behind the counter.
/// <para>
/// Every checkout test needs the same four things — a variant that exists, a stock ledger with a
/// known number in it, browsers that are genuinely separate visitors, and a session-less view of
/// the tables afterwards — so they are written once here and the tests themselves stay a sequence
/// of commercial statements.
/// </para>
/// <para>
/// The split of responsibilities is deliberate and worth stating, because it is what makes the
/// assertions mean anything. <strong>Everything a shopper does goes over HTTP</strong> into the
/// composed host: adding to the cart and checking out are the endpoints a storefront calls, driven
/// the way a storefront calls them. <strong>Everything a shopkeeper does goes straight to the
/// database</strong> through the fixture's session-less context: seeding a product, moving a price,
/// withdrawing a variant, and reading the ledger back. Reading the outcome through the same API
/// that produced it would let one bug hide another — an endpoint that both mis-reserves stock and
/// mis-reports it would look correct.
/// </para>
/// </summary>
internal sealed class Storefront : IDisposable
{
    private readonly PostgresFixture _fixture;

    public Storefront(PostgresFixture fixture)
    {
        _fixture = fixture;
        Host = new CheckoutHost(fixture.ConnectionString);
    }

    /// <summary>The composed API, in-process. Exposed so a test can assert on the wiring itself.</summary>
    public CheckoutHost Host { get; }

    public void Dispose() => Host.Dispose();

    // -------------------------------------------------------------------------------------------
    // Behind the counter.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts a variant in the catalog and a known quantity of it on the shelf.
    /// <para>
    /// Slug and SKU carry a fresh UUID because the whole assembly shares one container: tests that
    /// collided over a fixed SKU would fail in whichever order the runner happened to pick, which
    /// is the least useful kind of red.
    /// </para>
    /// </summary>
    /// <param name="name">Product name, so a failure message names the thing that was oversold.</param>
    /// <param name="onHand">Units in the warehouse. Reserved starts at zero.</param>
    /// <param name="priceMinorUnits">
    /// Unit price in cents. The default is deliberately not a round number ending in .01 to .05:
    /// the payment simulator reads the trailing cents of an order total as a scenario selector, so
    /// a price chosen carelessly could decline a payment a test expected to succeed. Every checkout
    /// here also passes an explicit scenario, which takes precedence, but a price that cannot
    /// collide costs nothing.
    /// </param>
    public async Task<StockedVariant> StockAsync(string name, int onHand, long priceMinorUnits = 4_500)
    {
        await using var db = _fixture.CreateContext();

        var product = new Product(
            $"race-{Guid.CreateVersion7():N}",
            name,
            "Written by the checkout concurrency tests.",
            "checkout");

        var variant = product.AddVariant($"RACE-{Guid.CreateVersion7():N}"[..20], "One size", new Money(priceMinorUnits));

        db.Products.Add(product);
        db.StockItems.Add(new StockItem(variant.Id, onHand));
        await db.SaveChangesAsync();

        return new StockedVariant(variant.Id, variant.Sku, name, priceMinorUnits);
    }

    /// <summary>The stock ledger as the database holds it, read outside every session.</summary>
    public async Task<Ledger> LedgerAsync(StockedVariant variant)
    {
        await using var db = _fixture.CreateContext();

        var stock = await db.StockItems.AsNoTracking().SingleAsync(item => item.VariantId == variant.VariantId);

        return new Ledger(stock.OnHand, stock.Reserved);
    }

    /// <summary>
    /// Takes units off the shelf without going through checkout, standing in for a shopper whose
    /// order is already in flight. Uses the same guarded UPDATE the checkout does, so the fixture
    /// cannot set up a state the application could not have produced.
    /// </summary>
    public async Task ReserveElsewhereAsync(StockedVariant variant, int quantity)
    {
        await using var db = _fixture.CreateContext();

        var reserved = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE stock_items
             SET reserved = reserved + {quantity}
             WHERE variant_id = {variant.VariantId}
               AND on_hand - reserved >= {quantity}
             """);

        Assert.Equal(1, reserved);
    }

    /// <summary>Moves a price in the catalog, the way an admin screen would.</summary>
    public async Task<StockedVariant> RepriceAsync(StockedVariant variant, long newPriceMinorUnits)
    {
        await using var db = _fixture.CreateContext();

        var row = await db.ProductVariants.SingleAsync(entity => entity.Id == variant.VariantId);
        row.Reprice(new Money(newPriceMinorUnits));
        await db.SaveChangesAsync();

        return variant with { UnitPriceMinorUnits = newPriceMinorUnits };
    }

    /// <summary>Withdraws a variant from sale, which is a soft delete rather than a row disappearing.</summary>
    public async Task WithdrawAsync(StockedVariant variant, DateTimeOffset now)
    {
        await using var db = _fixture.CreateContext();

        var row = await db.ProductVariants.SingleAsync(entity => entity.Id == variant.VariantId);
        row.SoftDelete(now);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Every order for a variant, whoever placed it.
    /// <para>
    /// The DemoTenancy filter is suppressed by name — and only that one, so soft-deleted rows stay
    /// hidden — because the fixture's context has no session bound and the filter therefore fails
    /// closed and would count zero. Counting zero is the correct answer to "which of these orders
    /// may this caller see" and a useless answer to "how many orders exist", which is the question
    /// every oversell assertion is actually asking.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<OrderRow>> OrdersForAsync(StockedVariant variant)
    {
        await using var db = _fixture.CreateContext();

        return await db.Orders
            .AsNoTracking()
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Where(order => order.Lines.Any(line => line.VariantId == variant.VariantId))
            .SelectMany(order => order.Lines
                .Where(line => line.VariantId == variant.VariantId)
                .Select(line => new OrderRow(
                    order.OrderNumber,
                    order.Status.ToString(),
                    order.DemoSessionId,
                    order.IdempotencyKey,
                    line.Quantity,
                    order.Captured.Amount)))
            .ToListAsync();
    }

    /// <summary>Every stock reservation raised for a variant, with the status the reaper reads.</summary>
    public async Task<IReadOnlyList<ReservationRow>> ReservationsForAsync(StockedVariant variant)
    {
        await using var db = _fixture.CreateContext();

        return await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.VariantId == variant.VariantId)
            .Select(reservation => new ReservationRow(
                reservation.OrderId,
                reservation.Quantity,
                reservation.Status.ToString()))
            .ToListAsync();
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

    /// <summary>A crowd. Opened concurrently, because opening fifty browsers in sequence is slow.</summary>
    public async Task<Shopper[]> NewShoppersAsync(int count) =>
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => NewShopperAsync()));

    /// <summary>
    /// Runs <paramref name="attempt"/> <paramref name="count"/> times at once, released together.
    /// <para>
    /// The gate is the point. Started in a loop, request one is usually finished before request two
    /// is written, and the test would prove only that a shop can sell things one at a time. Every
    /// attempt is queued to the thread pool and parked on the same task, so releasing it hands the
    /// whole crowd to the server with no ordering between them — which is the condition under which
    /// a read-then-write reservation actually oversells.
    /// </para>
    /// </summary>
    public static async Task<T[]> AllAtOnceAsync<T>(int count, Func<int, Task<T>> attempt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, count)
            .Select(index => Task.Run(async () =>
            {
                await gate.Task;
                return await attempt(index);
            }))
            .ToArray();

        gate.SetResult();

        return await Task.WhenAll(attempts);
    }
}

/// <summary>A variant that exists in the catalog and has a stock ledger row.</summary>
/// <param name="VariantId">The buyable id a cart line points at.</param>
/// <param name="Sku">As the catalog holds it; what a shortfall names.</param>
/// <param name="ProductName">For readable failures.</param>
/// <param name="UnitPriceMinorUnits">Current catalog price, in cents.</param>
internal sealed record StockedVariant(Guid VariantId, string Sku, string ProductName, long UnitPriceMinorUnits);

/// <summary>The two numbers the whole suite is about.</summary>
/// <param name="OnHand">Physical units.</param>
/// <param name="Reserved">Units promised to an order that has not shipped.</param>
internal sealed record Ledger(int OnHand, int Reserved)
{
    /// <summary>What the next shopper may still take.</summary>
    public int Available => OnHand - Reserved;
}

/// <summary>One order line as the database holds it, flattened for assertions.</summary>
internal sealed record OrderRow(
    string OrderNumber,
    string Status,
    Guid DemoSessionId,
    string IdempotencyKey,
    int Quantity,
    long CapturedAmount);

/// <summary>One stock reservation as the database holds it.</summary>
internal sealed record ReservationRow(Guid OrderId, int Quantity, string Status);

/// <summary>
/// One visitor with one cookie jar. Everything it does is an HTTP call a storefront would make.
/// </summary>
internal sealed class Shopper(HttpClient client)
{
    /// <summary>The browser. Owned by the factory and disposed with the host.</summary>
    public HttpClient Client { get; } = client;

    /// <summary>Adds units to this visitor's cart, and refuses to continue if that did not work.</summary>
    public async Task AddToCartAsync(StockedVariant variant, int quantity = 1)
    {
        using var response = await Client.PostAsJsonAsync(
            "/api/cart/items",
            new { variantId = variant.VariantId, quantity });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>This visitor's cart, as the storefront would read it.</summary>
    public async Task<CartView> CartAsync()
    {
        using var response = await Client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<CartView>()
               ?? throw new InvalidOperationException("The cart endpoint answered with a null JSON body.");
    }

    /// <summary>
    /// Places the order.
    /// <para>
    /// The scenario is passed explicitly on every call rather than left to default, because the
    /// simulator otherwise picks one from the trailing cents of the order total — a real feature of
    /// the demo, and a trap for a test whose totals move when a price or a quantity changes. A test
    /// that means "the payment succeeds" should say so.
    /// </para>
    /// </summary>
    /// <param name="idempotencyKey">Sent in the conventional header, as a client library would.</param>
    /// <param name="scenario">Succeed, Decline, Abandon, Duplicate, Delay or Reorder.</param>
    public async Task<HttpResponseMessage> CheckoutAsync(string idempotencyKey, string scenario = "Succeed")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout")
        {
            Content = JsonContent.Create(new
            {
                paymentScenario = scenario,
                shippingAddress = new
                {
                    recipient = "Ingrid Halvorsen",
                    line1 = "12 Bryggen",
                    city = "Bergen",
                    postalCode = "5003",
                    countryCode = "NO",
                },
            }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await Client.SendAsync(request);
    }
}

/// <summary>
/// The shapes these tests read off the wire.
/// <para>
/// Deliberately not the API's own contracts, for the reason the isolation tests give: a wire-level
/// test that shared the server's types would let a renamed JSON property pass unnoticed on both
/// sides. Only the fields being asserted on are declared.
/// </para>
/// </summary>
/// <param name="TotalQuantity">Units across every line.</param>
/// <param name="IsEmpty">Whether there is anything to check out.</param>
/// <param name="HasPriceChanges">Whether a line's price has moved since it was added.</param>
/// <param name="HasUnavailableLines">Whether a line names a variant the catalog no longer sells.</param>
internal sealed record CartView(
    int TotalQuantity,
    bool IsEmpty,
    bool HasPriceChanges,
    bool HasUnavailableLines);

/// <summary>An order as the checkout and retrieval endpoints render it.</summary>
internal sealed record OrderView(
    string OrderNumber,
    string Status,
    MoneyView Total,
    MoneyView Captured,
    IReadOnlyList<OrderLineView> Lines,
    string RetrievalToken,
    string RetrievalPath,
    PaymentView? Payment);

/// <summary>One line of a placed order.</summary>
internal sealed record OrderLineView(Guid VariantId, string Sku, int Quantity, MoneyView UnitPrice);

/// <summary>Minor units plus the string the storefront renders.</summary>
internal sealed record MoneyView(long Amount, string Currency, string Display);

/// <summary>What the gateway answered, as reported once on the checkout response.</summary>
internal sealed record PaymentView(
    string Outcome,
    string GatewayReference,
    string? DeclineReason,
    bool Captured,
    bool AwaitsSettlement);

/// <summary>
/// A problem response, with the two extensions checkout adds to it. RFC 9457 extension members sit
/// at the top level of the document, which is why they are properties here rather than a bag.
/// </summary>
internal sealed record ProblemView(
    string? Title,
    string? Detail,
    int? Status,
    ShortfallView? Shortfall,
    IReadOnlyList<PriceChangeView>? PriceChanges,
    PaymentView? Payment,
    string? OrderNumber);

/// <summary>The line that lost the race, as the 409 reports it.</summary>
internal sealed record ShortfallView(Guid VariantId, string Sku, string DisplayName, int Requested, int? Available);

/// <summary>One line whose price moved, as the 409 reports it.</summary>
internal sealed record PriceChangeView(
    Guid VariantId,
    string Sku,
    MoneyView Was,
    MoneyView? Now,
    MoneyView? Difference,
    bool NoLongerSold);

/// <summary>Reading a response body without repeating the null check in every test.</summary>
internal static class ResponseReader
{
    /// <summary>The order on a 200, 201 or 202.</summary>
    public static async Task<OrderView> OrderAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<OrderView>()
        ?? throw new InvalidOperationException("The checkout endpoint answered with a null JSON body.");

    /// <summary>The problem document on a 4xx.</summary>
    public static async Task<ProblemView> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ProblemView>()
        ?? throw new InvalidOperationException("The checkout endpoint answered with a null JSON body.");
}
