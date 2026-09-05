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
    /// <b>Two queries and an in-memory join, deliberately.</b> Expressed as a single LINQ
    /// <c>Join</c> across the overlay and the catalog it does not translate — the projection
    /// reaches a navigation on the joined side and EF gives up at runtime, which is a 500 on the
    /// page rather than a compile error. The set being joined is one visitor's own overrides, so
    /// the cost of doing it here is a few rows; the cost of the clever version was a page that
    /// worked in a unit test and broke in a browser.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AdminOverrideRow>> OverridesAsync(CancellationToken cancellationToken = default)
    {
        // Filtered to the caller by DemoTenancy, with no predicate written here.
        var overrides = await db.Set<DemoCatalogPriceOverride>()
            .AsNoTracking()
            .Select(over => new { over.VariantId, over.PriceAmount })
            .ToListAsync(cancellationToken);

        if (overrides.Count == 0)
        {
            return [];
        }

        var variantIds = overrides.Select(over => over.VariantId).ToArray();

        var variants = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variantIds.Contains(variant.Id) && variant.DeletedAt == null)
            .Select(variant => new
            {
                variant.Id,
                variant.Sku,
                VariantName = variant.Name,
                ProductName = variant.Product!.Name,
                variant.Product.Category,
                SeedAmount = variant.Price.Amount,
                Currency = variant.Price.Currency,
            })
            .ToDictionaryAsync(row => row.Id, cancellationToken);

        return
        [
            .. overrides
                .Where(over => variants.ContainsKey(over.VariantId))
                .Select(over =>
                {
                    var variant = variants[over.VariantId];

                    return new AdminOverrideRow(
                        variant.Id,
                        variant.Sku,
                        variant.ProductName,
                        variant.VariantName,
                        variant.Category,
                        variant.SeedAmount,
                        over.PriceAmount,
                        variant.Currency);
                })
                .OrderBy(row => row.Sku, StringComparer.Ordinal)
        ];
    }

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
