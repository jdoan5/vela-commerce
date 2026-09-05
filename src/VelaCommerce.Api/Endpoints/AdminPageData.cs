using Microsoft.EntityFrameworkCore;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Everything the admin pages read, as projections.
/// <para>
/// <b>The pages never see a <c>DbContext</c>.</b> They inject this, it hands back records, and an
/// architecture rule keeps it that way — a Razor component holding a unit of work is a component
/// that can start a query inside a render, which is a hard thing to notice and a harder one to
/// bound. Everything here is <c>AsNoTracking</c> and shaped for one screen.
/// </para>
/// <para>
/// <b>No query below names a session.</b> The <c>DemoTenancy</c> filter narrows orders and price
/// overrides to the caller before these queries run, so the console shows one visitor's shop
/// because of the model rather than because of a <c>WHERE</c> somebody remembered to write. That is
/// also why the admin cookie is a front door rather than a wall: delete the policy and these
/// queries still return only the caller's rows.
/// </para>
/// </summary>
public sealed class AdminPageData(VelaCommerceDbContext db)
{
    /// <summary>This visitor's orders, newest first, with what a grid needs and nothing more.</summary>
    public async Task<IReadOnlyList<AdminOrderRow>> OrdersAsync(CancellationToken cancellationToken = default) =>
        await db.Orders
            .AsNoTracking()
            .OrderByDescending(order => order.PlacedAt)
            .Select(order => new AdminOrderRow(
                order.OrderNumber,
                order.Status.ToString(),
                order.PlacedAt,
                order.Lines.Sum(line => line.Quantity),
                order.Captured.Amount,
                order.Refunded.Amount,
                order.Currency,
                order.Status == OrderStatus.Paid))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The price overrides this visitor holds, each beside the shared price it covers, so the page
    /// can show the delta rather than asking the reader to hold two numbers in their head.
    /// <para>
    /// The query is <c>EffectiveCatalogPrices</c>'s, not this class's. That file claims to be the
    /// only place that reads the overlay, and this reader is what made the claim false for a day:
    /// it named the entity directly to dodge a LINQ translation failure. The read moved there, the
    /// entity went <c>internal</c>, and <c>CatalogOverlayRules</c> now fails if either comes back.
    /// All that is left here is the shaping and the order, which is what a page reader is for.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AdminOverrideRow>> OverridesAsync(CancellationToken cancellationToken = default) =>
        [.. (await db.OverriddenVariantsAsync(cancellationToken))
            .Select(moved => new AdminOverrideRow(
                moved.VariantId,
                moved.Sku,
                moved.ProductName,
                moved.VariantName,
                moved.Category,
                moved.SeedAmount,
                moved.YourAmount,
                moved.Currency))
            // Ordinal, so the SKU order a reviewer sees is the same on every machine.
            .OrderBy(row => row.Sku, StringComparer.Ordinal)];

    /// <summary>The categories a bulk reprice may name, read from the catalog rather than hardcoded.</summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken = default) =>
        await db.Products
            .AsNoTracking()
            .Select(product => product.Category)
            .Distinct()
            .OrderBy(category => category)
            .ToListAsync(cancellationToken);
}

/// <summary>One order, as the admin grid shows it.</summary>
/// <param name="CanPack">Precomputed so the page has no domain rule of its own to get wrong.</param>
public sealed record AdminOrderRow(
    string OrderNumber,
    string Status,
    DateTimeOffset PlacedAt,
    int Units,
    long CapturedAmount,
    long RefundedAmount,
    string Currency,
    bool CanPack);

/// <summary>One price this visitor has moved, beside the price everybody else still sees.</summary>
public sealed record AdminOverrideRow(
    Guid VariantId,
    string Sku,
    string ProductName,
    string VariantName,
    string Category,
    long SeedAmount,
    long YourAmount,
    string Currency)
{
    public long Delta => YourAmount - SeedAmount;
}
