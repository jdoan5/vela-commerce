// The catalog group is deliberately read-only and anonymous. It is the one path that has to
// work while everything else is cold: no session cookie, no auth handler, no writes, no outbox,
// nothing that would drag a warm-up dependency onto a request that only wants to draw a grid.
// If a shopper's very first click has to wake this container, these three endpoints are what
// it wakes for — so they stay the cheapest thing in the app, and any temptation to add a write,
// a header requirement or a per-user branch here belongs in another group.

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using VelaCommerce.Api.Contracts;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Registration for the public catalog surface: browse, one product, and the category facets.
/// <para>
/// Every query here projects straight into a contract record. Nothing in this file materialises
/// a domain entity, which is what keeps the aggregate free to change its private shape without
/// silently changing the public JSON — and stops a lazily-loaded navigation from turning one
/// grid render into a hundred round trips.
/// </para>
/// </summary>
public static class CatalogEndpoints
{
    private const int DefaultPageSize = 24;
    private const int MaxPageSize = 100;

    /// <summary>
    /// Escape character passed explicitly to ILIKE. Npgsql's two-argument overload emits
    /// <c>ESCAPE ''</c>, which switches PostgreSQL's escaping off entirely and would turn the
    /// escaping done in <see cref="EscapeForLike"/> into literal backslashes in the pattern.
    /// </summary>
    private const string LikeEscapeCharacter = "\\";

    private const string SortByName = "name";
    private const string SortByPriceAscending = "price_asc";
    private const string SortByPriceDescending = "price_desc";

    /// <summary>
    /// Maps the catalog group. Called by the host so this file never has to know how the app is
    /// composed, and so the group can be moved behind a different prefix without editing handlers.
    /// </summary>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app
            .MapGroup("/api/catalog")
            .WithTags("Catalog")
            .AllowAnonymous();

        catalog.MapGet("/products", ListProductsAsync)
            .WithName("ListProducts")
            .WithSummary("Browse the catalog")
            .WithDescription(
                "Returns a page of products with a variant count and a 'from' price. " +
                "Paging inputs are clamped rather than rejected: page floors at 1 and pageSize is " +
                $"held to 1..{MaxPageSize} (default {DefaultPageSize}), so a mistyped query string " +
                $"still renders a grid. Sort accepts '{SortByName}' (the default), " +
                $"'{SortByPriceAscending}' or '{SortByPriceDescending}'; anything unrecognised also " +
                $"falls back to '{SortByName}'. Every ordering carries a unique tiebreaker, so a " +
                "product cannot appear on two pages or vanish between them.");

        catalog.MapGet("/products/{slug}", GetProductBySlugAsync)
            .WithName("GetProductBySlug")
            .WithSummary("Get one product by slug")
            .WithDescription(
                "Returns the product with every live variant, cheapest first, and each variant's " +
                "current availability. Availability is a read-time snapshot for rendering only - " +
                "the reservation that decides who actually gets the last unit happens at checkout. " +
                "Responds 404 when the slug matches nothing live.");

        catalog.MapGet("/categories", ListCategoriesAsync)
            .WithName("ListCategories")
            .WithSummary("List category facets")
            .WithDescription(
                "Returns every distinct category that still has at least one live product, with " +
                "its product count, ordered by name. Counts are included so the storefront can grey " +
                "out filters that lead nowhere without one request per facet.");

        return app;
    }

    private static async Task<Ok<PagedResponse<ProductSummaryResponse>>> ListProductsAsync(
        VelaCommerceDbContext db,
        string? category,
        string? q,
        string? sort,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        // Clamp, never throw. A bad page size in a shared link should still show the shopper a
        // storefront; a 400 here would be technically correct and practically a blank screen.
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Products
            .AsNoTracking()
            .Where(p => p.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(category))
        {
            // Compared case-insensitively because the category arrives from a URL a human may have
            // typed or a link that lower-cased it, but it is an equality test, not a pattern.
            var normalizedCategory = category.Trim().ToLowerInvariant();
            query = query.Where(p => p.Category.ToLower() == normalizedCategory);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // ILIKE rather than lower(col) LIKE: it leaves the column untouched, so a pg_trgm GIN
            // index can serve it. The term is escaped because a shopper searching for "50%" must
            // get products, not the whole catalog.
            var pattern = $"%{EscapeForLike(q.Trim())}%";
            query = query.Where(p =>
                EF.Functions.ILike(p.Name, pattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(p.Description, pattern, LikeEscapeCharacter));
        }

        var total = await query.CountAsync(cancellationToken);

        // Price sorts run against the minor-unit column, never a formatted string, or "$9.00"
        // would sort after "$10.00". Slug is the tiebreaker on every branch: without a unique
        // final key, two equally-priced products can swap places between page 1 and page 2 and
        // the shopper sees one twice and the other never.
        query = sort?.Trim().ToLowerInvariant() switch
        {
            SortByPriceAscending => query
                .OrderBy(p => p.Variants.Where(v => v.DeletedAt == null).Min(v => (long?)v.Price.Amount))
                .ThenBy(p => p.Slug),
            SortByPriceDescending => query
                .OrderByDescending(p => p.Variants.Where(v => v.DeletedAt == null).Min(v => (long?)v.Price.Amount))
                .ThenBy(p => p.Slug),
            _ => query
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Slug),
        };

        // Four correlated subqueries per row, all hitting the product_id index, in exchange for a
        // single round trip and no client-side grouping. At a hard ceiling of 100 rows that is the
        // cheaper half of the trade against a sleeping database five hundred milliseconds away.
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductSummaryResponse(
                p.Id,
                p.Slug,
                p.Name,
                p.Description,
                p.Category,
                p.Variants.Count(v => v.DeletedAt == null),
                MoneyDto.Optional(
                    p.Variants
                        .Where(v => v.DeletedAt == null)
                        .Min(v => (long?)v.Price.Amount),
                    p.Variants
                        .Where(v => v.DeletedAt == null)
                        .OrderBy(v => v.Price.Amount)
                        .Select(v => v.Price.Currency)
                        .FirstOrDefault()),
                p.Variants
                    .Where(v => v.DeletedAt == null && v.ImageUrl != null)
                    .OrderBy(v => v.Price.Amount)
                    .Select(v => v.ImageUrl)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PagedResponse<ProductSummaryResponse>(items, page, pageSize, total));
    }

    private static async Task<Results<Ok<ProductDetailResponse>, NotFound>> GetProductBySlugAsync(
        string slug,
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        // Product normalises its slug to lower-case on construction, so the lookup normalises too
        // rather than relying on the caller to have got the casing right.
        var normalizedSlug = slug.Trim().ToLowerInvariant();

        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Slug == normalizedSlug)
            .Select(p => new ProductDetailResponse(
                p.Id,
                p.Slug,
                p.Name,
                p.Description,
                p.Category,
                p.Attributes,
                MoneyDto.Optional(
                    p.Variants
                        .Where(v => v.DeletedAt == null)
                        .Min(v => (long?)v.Price.Amount),
                    p.Variants
                        .Where(v => v.DeletedAt == null)
                        .OrderBy(v => v.Price.Amount)
                        .Select(v => v.Price.Currency)
                        .FirstOrDefault()),
                p.Variants
                    .Where(v => v.DeletedAt == null)
                    .OrderBy(v => v.Price.Amount)
                    .ThenBy(v => v.Sku)
                    .Select(v => new ProductVariantResponse(
                        v.Id,
                        v.Sku,
                        v.Name,
                        new MoneyDto(v.Price.Amount, v.Price.Currency),
                        v.ImageUrl,
                        // No navigation from a variant to its stock row on purpose: inventory is a
                        // separate aggregate and must not become something a catalog read can lazily
                        // drag in. A missing row means nothing was ever stocked, which reads as 0.
                        db.StockItems
                            .Where(s => s.VariantId == v.Id && s.DeletedAt == null)
                            .Select(s => s.OnHand - s.Reserved)
                            .FirstOrDefault()))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(product);
    }

    private static async Task<Ok<IReadOnlyList<CategoryResponse>>> ListCategoriesAsync(
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        // Grouped in the database rather than fetched and counted in memory: the facet list is
        // small, but the table behind it is not, and the difference is one row per category on
        // the wire instead of one row per product.
        var categories = await db.Products
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .GroupBy(p => p.Category)
            .OrderBy(g => g.Key)
            .Select(g => new CategoryResponse(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<CategoryResponse>>(categories);
    }

    /// <summary>
    /// Neutralises LIKE wildcards in shopper input, so a search for "100%" searches for the text
    /// and not for everything. Pairs with <see cref="LikeEscapeCharacter"/>, which has to be passed
    /// to ILIKE explicitly. The backslash is doubled first, or it would escape the escapes added
    /// after it and "a\b" would silently swallow the following character.
    /// </summary>
    private static string EscapeForLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
