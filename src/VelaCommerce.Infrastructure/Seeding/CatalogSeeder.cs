using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Infrastructure.Seeding;

/// <summary>
/// Loads the generated catalog into an empty database.
/// <para>
/// Deliberately refuses to run against a non-empty catalog rather than upserting.
/// Re-seeding is the demo-reset job's business, and it must never quietly rewrite
/// rows a shopper is looking at.
/// </para>
/// </summary>
public sealed class CatalogSeeder(VelaCommerceDbContext db, ILogger<CatalogSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <returns>Number of products written; zero if the catalog was already populated.</returns>
    public async Task<int> SeedAsync(string seedFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(seedFilePath))
        {
            logger.LogWarning("No seed file at {Path}; skipping catalog seed.", seedFilePath);
            return 0;
        }

        if (await db.Products.AnyAsync(ct))
        {
            logger.LogInformation("Catalog already populated; skipping seed.");
            return 0;
        }

        await using var stream = File.OpenRead(seedFilePath);
        var document = await JsonSerializer.DeserializeAsync<SeedDocument>(stream, JsonOptions, ct)
                       ?? throw new InvalidOperationException($"Seed file at {seedFilePath} did not deserialize.");

        var products = new List<Product>(document.Products.Count);
        var stockItems = new List<StockItem>(document.Metadata.VariantCount);

        foreach (var seeded in document.Products)
        {
            var product = new Product(seeded.Slug, seeded.Name, seeded.Description, seeded.Category);

            foreach (var (key, value) in seeded.Attributes)
                product.Attributes[key] = value;

            foreach (var seededVariant in seeded.Variants)
            {
                var variant = product.AddVariant(
                    seededVariant.Sku,
                    seededVariant.Name,
                    new Money(seededVariant.Price.AmountMinorUnits, seededVariant.Price.Currency),
                    seededVariant.ImageUrl);

                // Ids are assigned by the domain constructors (UUIDv7), so stock can be
                // linked here without a round trip to the database.
                stockItems.Add(new StockItem(variant.Id, seededVariant.StockOnHand));
            }

            products.Add(product);
        }

        db.Products.AddRange(products);
        db.StockItems.AddRange(stockItems);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Seeded {Products} products, {Variants} variants, {Stock} stock rows (last-unit demo SKU: {Sku}).",
            products.Count, products.Sum(p => p.Variants.Count), stockItems.Count,
            document.Metadata.LastUnitDemoSku ?? "none");

        return products.Count;
    }
}
