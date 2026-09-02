using Microsoft.EntityFrameworkCore;
using Npgsql;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// Proves the invariants are enforced by PostgreSQL, not merely by the C# that usually
/// runs first. Each test bypasses the domain deliberately and writes the illegal row
/// straight through EF or raw SQL — if the database were the only thing standing
/// between a shopper and an oversold item, would it hold?
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DatabaseInvariantTests(PostgresFixture fixture)
{
    /// <summary>Passed as a parameter so no literal brace appears in a raw interpolated string.</summary>
    private const string EmptyJson = "{}";

    private static (Product Product, ProductVariant Variant) NewProduct(string slug, string sku)
    {
        var product = new Product(slug, "Kattegat Dock Line", "Three-strand nylon.", "rigging-and-rope");
        var variant = product.AddVariant(sku, "20m", new Money(10_295));
        return (product, variant);
    }

    [Fact]
    public async Task Stock_cannot_be_reserved_beyond_what_is_on_hand()
    {
        await using var db = fixture.CreateContext();
        var (product, variant) = NewProduct($"dock-line-{Guid.CreateVersion7():N}", $"SKU-{Guid.CreateVersion7():N}"[..20]);
        var stock = new StockItem(variant.Id, onHand: 1);

        db.Products.Add(product);
        db.StockItems.Add(stock);
        await db.SaveChangesAsync();

        // Reserving the one unit that exists is fine.
        var reserved = await db.Database.ExecuteSqlAsync(
            $"UPDATE stock_items SET reserved = 1 WHERE id = {stock.Id} AND on_hand - reserved >= 1");
        Assert.Equal(1, reserved);

        // A second shopper reaching the same row must be refused by the database itself.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE stock_items SET reserved = 2 WHERE id = {stock.Id}"));

        Assert.Equal("23514", ex.SqlState); // check_violation
        Assert.Contains("reserved_within_on_hand", ex.ConstraintName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_conditional_update_is_what_makes_the_last_unit_race_safe()
    {
        await using var db = fixture.CreateContext();
        var (product, variant) = NewProduct($"dock-line-{Guid.CreateVersion7():N}", $"SKU-{Guid.CreateVersion7():N}"[..20]);
        var stock = new StockItem(variant.Id, onHand: 1);

        db.Products.Add(product);
        db.StockItems.Add(stock);
        await db.SaveChangesAsync();

        // Two shoppers issue the identical guarded UPDATE. Exactly one may win, and the
        // loser must be told by a row count of zero rather than by an exception.
        var first = await db.Database.ExecuteSqlAsync(
            $"UPDATE stock_items SET reserved = reserved + 1 WHERE id = {stock.Id} AND on_hand - reserved >= 1");
        var second = await db.Database.ExecuteSqlAsync(
            $"UPDATE stock_items SET reserved = reserved + 1 WHERE id = {stock.Id} AND on_hand - reserved >= 1");

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task A_product_slug_cannot_be_reused()
    {
        var slug = $"dock-line-{Guid.CreateVersion7():N}";

        await using (var db = fixture.CreateContext())
        {
            db.Products.Add(NewProduct(slug, $"SKU-{Guid.CreateVersion7():N}"[..20]).Product);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            db.Products.Add(NewProduct(slug, $"SKU-{Guid.CreateVersion7():N}"[..20]).Product);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            var pg = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pg.SqlState); // unique_violation
            Assert.Contains("ux_products_slug", pg.ConstraintName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_refund_cannot_exceed_what_was_captured()
    {
        await using var db = fixture.CreateContext();

        // Written as raw SQL because the domain refuses to build this state at all —
        // which is exactly why the database needs its own opinion about it.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO orders (id, demo_session_id, order_number, idempotency_key, status, currency,
                                shipping_address, placed_at, shipping_amount, shipping_currency,
                                tax_amount, tax_currency, captured_amount, captured_currency,
                                refunded_amount, refunded_currency)
            VALUES (gen_random_uuid(), gen_random_uuid(), {$"VELA-{Guid.CreateVersion7():N}"[..16]}, {$"key-{Guid.CreateVersion7():N}"},
                    1, 'USD', {EmptyJson}::jsonb, now(), 0, 'USD', 0, 'USD', 1000, 'USD', 5000, 'USD')
            """));

        Assert.Equal("23514", ex.SqlState);
        Assert.Contains("refund_within_capture", ex.ConstraintName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_double_submitted_checkout_creates_one_order_not_two()
    {
        await using var db = fixture.CreateContext();
        var session = Guid.CreateVersion7();
        var key = $"checkout-{Guid.CreateVersion7():N}";

        async Task<int> Submit(string orderNumber) => await db.Database.ExecuteSqlAsync($"""
            INSERT INTO orders (id, demo_session_id, order_number, idempotency_key, status, currency,
                                shipping_address, placed_at, shipping_amount, shipping_currency,
                                tax_amount, tax_currency, captured_amount, captured_currency,
                                refunded_amount, refunded_currency)
            VALUES (gen_random_uuid(), {session}, {orderNumber}, {key}, 0, 'USD', {EmptyJson}::jsonb, now(),
                    0, 'USD', 0, 'USD', 0, 'USD', 0, 'USD')
            """);

        Assert.Equal(1, await Submit($"VELA-{Guid.CreateVersion7():N}"[..16]));

        // The shopper double-clicked. Same session, same key, new order number.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Submit($"VELA-{Guid.CreateVersion7():N}"[..16]));

        Assert.Equal("23505", ex.SqlState);
        Assert.Contains("idempotency_key", ex.ConstraintName, StringComparison.Ordinal);

        var count = await db.Orders.CountAsync(o => o.DemoSessionId == session);
        Assert.Equal(1, count);
    }
}
