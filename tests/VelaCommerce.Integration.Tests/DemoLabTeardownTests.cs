using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VelaCommerce.Api.Endpoints;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The Demo Lab seeds a private product, races it, and deletes everything afterwards with the
/// query filters suppressed — the only place in the application that removes rows it does not own.
/// <para>
/// That teardown once deleted real visitors' paid orders. The fixture product is listed in the
/// public catalog for as long as a run takes, so a real shopper can buy it, and the teardown
/// removed every order referencing the fixture on the assumption that only throwaway visitors
/// could hold one. These tests exist because that assumption was invisible to the rest of the
/// suite: reproducing it over HTTP means winning a race against a product that lives for
/// milliseconds.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DemoLabTeardownTests(PostgresFixture fixture)
{
    private static readonly ShippingAddress Address = new()
    {
        Recipient = "Real Visitor",
        Line1 = "1 Quay Street",
        City = "Bristol",
        PostalCode = "BS1 4RN",
        CountryCode = "GB"
    };

    private static readonly DateTimeOffset PlacedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Seeds a product shaped exactly like a lab fixture and returns the handle teardown takes.</summary>
    private static async Task<(DemoLabEndpoints.LabFixture Fixture, Guid VariantId)> SeedFixtureAsync(
        VelaCommerceDbContextAccessor db)
    {
        var slug = $"lab-fixture-{Guid.CreateVersion7():N}";
        var product = new Product(slug, "Lab Fixture Jib", "Seeded by a lab run.", "rope-and-rigging");
        var variant = product.AddVariant($"LAB-{Guid.NewGuid():N}"[..18], "Standard", new Money(4_200));

        db.Context.Products.Add(product);
        db.Context.StockItems.Add(new StockItem(variant.Id, onHand: 5));
        await db.Context.SaveChangesAsync();

        var handle = new DemoLabEndpoints.LabFixture(
            product.Id,
            slug,
            [new DemoLabEndpoints.LabFixtureVariant(variant.Id, variant.Sku, "Lab Fixture Jib", 4_200, 5)]);

        return (handle, variant.Id);
    }

    [Fact]
    public async Task A_real_visitors_paid_order_survives_the_teardown_of_a_fixture_they_bought()
    {
        await using var db = new VelaCommerceDbContextAccessor(fixture);
        var (handle, variantId) = await SeedFixtureAsync(db);

        // A real shopper — NOT one of the throwaway visitors the run minted — buys the fixture
        // while it is briefly listed, exactly as happened when this was reported.
        var shopper = Guid.CreateVersion7();
        var cart = new Cart(shopper);
        cart.AddItem(variantId, "LAB-SKU", "Lab Fixture Jib", new Money(4_200), 1);
        db.Context.Carts.Add(cart);

        var order = Order.FromCart(cart, "VELA-REAL001", "real-key-1", Address, Money.Zero(), Money.Zero(), PlacedAt);
        order.MarkPaid(order.Total, "pay_teardown_fixture", PlacedAt.AddSeconds(1));
        db.Context.Orders.Add(order);
        await db.Context.SaveChangesAsync();

        // The run tore down, knowing only its own sessions — of which this shopper is not one.
        await DemoLabEndpoints.DestroyFixtureAsync(
            db.Context, handle, fixtureSessionIds: [Guid.CreateVersion7()], NullLogger.Instance, CancellationToken.None);

        var survivor = await db.Fresh().Orders
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.OrderNumber == "VELA-REAL001");

        Assert.NotNull(survivor);
        Assert.Equal(OrderStatus.Paid, survivor.Status);
    }

    [Fact]
    public async Task A_real_visitors_cart_keeps_its_other_items_when_a_fixture_line_is_removed()
    {
        await using var db = new VelaCommerceDbContextAccessor(fixture);
        var (handle, variantId) = await SeedFixtureAsync(db);

        // Their cart holds the fixture AND something real. Deleting the parent row took the
        // unrelated item with it — a two-line cart came back holding nothing.
        var shopper = Guid.CreateVersion7();
        var realVariantId = Guid.CreateVersion7();
        var cart = new Cart(shopper);
        cart.AddItem(variantId, "LAB-SKU", "Lab Fixture Jib", new Money(4_200), 1);
        cart.AddItem(realVariantId, "VC-REAL-01", "Kattegat Dock Line", new Money(10_295), 2);
        db.Context.Carts.Add(cart);
        await db.Context.SaveChangesAsync();

        await DemoLabEndpoints.DestroyFixtureAsync(
            db.Context, handle, fixtureSessionIds: [Guid.CreateVersion7()], NullLogger.Instance, CancellationToken.None);

        var survivor = await db.Fresh().Carts
            .IgnoreQueryFilters()
            .Include(entity => entity.Lines)
            .SingleOrDefaultAsync(entity => entity.DemoSessionId == shopper);

        Assert.NotNull(survivor);
        Assert.Equal(["VC-REAL-01"], survivor.Lines.Select(line => line.Sku));
    }

    [Fact]
    public async Task The_runs_own_rows_are_removed_and_the_touched_count_reports_the_difference()
    {
        await using var db = new VelaCommerceDbContextAccessor(fixture);
        var (handle, variantId) = await SeedFixtureAsync(db);

        var labVisitor = Guid.CreateVersion7();
        var labCart = new Cart(labVisitor);
        labCart.AddItem(variantId, "LAB-SKU", "Lab Fixture Jib", new Money(4_200), 1);
        db.Context.Carts.Add(labCart);

        var labOrder = Order.FromCart(labCart, "VELA-LAB0001", "lab-key-1", Address, Money.Zero(), Money.Zero(), PlacedAt);
        db.Context.Orders.Add(labOrder);
        await db.Context.SaveChangesAsync();

        var teardown = await DemoLabEndpoints.DestroyFixtureAsync(
            db.Context, handle, fixtureSessionIds: [labVisitor], NullLogger.Instance, CancellationToken.None);

        var gone = await db.Fresh().Orders
            .IgnoreQueryFilters()
            .AnyAsync(entity => entity.OrderNumber == "VELA-LAB0001");

        Assert.False(gone);

        // Nothing foreign was involved, so the number that exists to detect the bug reads zero —
        // and it is a measurement now, not the literal it used to be.
        Assert.Equal(0, teardown.SharedRowsTouched);
    }
}
