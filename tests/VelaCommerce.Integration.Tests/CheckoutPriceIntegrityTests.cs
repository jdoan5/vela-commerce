using System.Net;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence that nobody is charged a price they were not shown.
/// <para>
/// A cart line is a snapshot: it remembers what the item cost when it went in. Catalogs move. That
/// leaves a checkout three choices, and two of them are dishonest. Charging the live price changes
/// the total between the page the shopper read and the card they were charged. Charging the
/// remembered price sells at a number that may be weeks stale, and lets a cart become a way to
/// freeze a promotion forever. This one refuses, names every line that moved and by how much, and
/// asks the shopper to look again.
/// </para>
/// <para>
/// Both directions are tested. An implementation that only guarded against increases would pass a
/// suite that only tested increases, and would happily sell at last month's higher price to a
/// shopper who had been shown a discount.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CheckoutPriceIntegrityTests : IDisposable
{
    private readonly Storefront _shop;

    public CheckoutPriceIntegrityTests(PostgresFixture fixture) => _shop = new Storefront(fixture);

    /// <summary>Disposes the host, its clients and the in-memory key ring.</summary>
    public void Dispose() => _shop.Dispose();

    /// <summary>
    /// A price that went up while the item sat in the cart stops the checkout, says by how much,
    /// takes no stock — and does not leave the shopper stranded.
    /// <para>
    /// The last clause is half the test. A refusal a shopper cannot act on is a broken shop, so
    /// this walks the whole way out: the cart page reports the change, the checkout refuses with
    /// the difference, and once the line is re-added at the price the shopper can now see, the
    /// order goes through and is charged at exactly that price.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_price_that_rose_since_the_item_was_added_stops_the_checkout()
    {
        var oil = await _shop.StockAsync("Teak deck oil", onHand: 3, priceMinorUnits: 4_500);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(oil, 2);

        var repriced = await _shop.RepriceAsync(oil, 4_900);

        // The storefront can see it before the shopper reaches payment, which is where a shopper
        // deserves to learn it.
        var cart = await shopper.CartAsync();
        Assert.True(cart.HasPriceChanges);
        Assert.False(cart.HasUnavailableLines);

        using (var refused = await shopper.CheckoutAsync($"reprice-{Guid.CreateVersion7():N}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var problem = await ResponseReader.ProblemAsync(refused);
            Assert.NotNull(problem.PriceChanges);

            var change = Assert.Single(problem.PriceChanges!);
            Assert.Equal(oil.VariantId, change.VariantId);
            Assert.Equal(oil.Sku, change.Sku);
            Assert.Equal(4_500, change.Was.Amount);
            Assert.Equal(4_900, change.Now?.Amount);
            Assert.Equal(400, change.Difference?.Amount);
            Assert.False(change.NoLongerSold);
        }

        // Refused before anything was taken: the price check runs before the first reservation, so
        // there is no window in which a shopper's rejected checkout is holding stock.
        Assert.Equal(new Ledger(OnHand: 3, Reserved: 0), await _shop.LedgerAsync(oil));
        Assert.Empty(await _shop.OrdersForAsync(oil));

        // The way out: drop the line and add it again, which is what a storefront's "update basket"
        // button does. The cart now holds the price the shopper has actually been shown.
        using (var removed = await shopper.Client.DeleteAsync($"/api/cart/items/{oil.VariantId}"))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }

        await shopper.AddToCartAsync(repriced, 2);
        Assert.False((await shopper.CartAsync()).HasPriceChanges);

        using var placed = await shopper.CheckoutAsync($"reprice-agreed-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var order = await ResponseReader.OrderAsync(placed);
        var line = Assert.Single(order.Lines);

        // Charged at the new price, which is the only number the shopper ever agreed to.
        Assert.Equal(4_900, line.UnitPrice.Amount);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(order.Total.Amount, order.Captured.Amount);
    }

    /// <summary>
    /// A price that fell is refused too. The shop does not quietly keep the difference.
    /// <para>
    /// This is the direction an implementation forgets, because the shopper is not being
    /// overcharged relative to what they expect — they are being overcharged relative to the shelf.
    /// The reported difference is signed, so a client can word the two cases differently without
    /// having to compare the amounts itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_price_that_fell_since_the_item_was_added_stops_the_checkout_as_well()
    {
        var lamp = await _shop.StockAsync("Anchor lamp", onHand: 3, priceMinorUnits: 8_000);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(lamp);

        await _shop.RepriceAsync(lamp, 6_000);

        using var refused = await shopper.CheckoutAsync($"discount-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var change = Assert.Single((await ResponseReader.ProblemAsync(refused)).PriceChanges!);
        Assert.Equal(8_000, change.Was.Amount);
        Assert.Equal(6_000, change.Now?.Amount);
        Assert.Equal(-2_000, change.Difference?.Amount);

        Assert.Equal(new Ledger(OnHand: 3, Reserved: 0), await _shop.LedgerAsync(lamp));
        Assert.Empty(await _shop.OrdersForAsync(lamp));
    }

    /// <summary>
    /// A variant withdrawn from sale is reported as no longer sold rather than as a price of zero,
    /// and is reported through the same 409 as a moved price.
    /// <para>
    /// One status code for both, deliberately: from the shopper's side they are the same problem —
    /// the cart no longer describes something that can be bought — and giving them separate codes
    /// would mean two error screens where one will do. The flag is what lets the wording differ.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_variant_withdrawn_from_sale_stops_the_checkout_and_says_so()
    {
        var whistle = await _shop.StockAsync("Bosun's whistle", onHand: 2);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(whistle);

        await _shop.WithdrawAsync(whistle, DateTimeOffset.UtcNow);

        var cart = await shopper.CartAsync();
        Assert.True(cart.HasUnavailableLines);

        using var refused = await shopper.CheckoutAsync($"withdrawn-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var change = Assert.Single((await ResponseReader.ProblemAsync(refused)).PriceChanges!);
        Assert.Equal(whistle.VariantId, change.VariantId);
        Assert.True(change.NoLongerSold);
        Assert.Null(change.Now);
        Assert.Null(change.Difference);

        Assert.Equal(new Ledger(OnHand: 2, Reserved: 0), await _shop.LedgerAsync(whistle));
        Assert.Empty(await _shop.OrdersForAsync(whistle));
    }
}
