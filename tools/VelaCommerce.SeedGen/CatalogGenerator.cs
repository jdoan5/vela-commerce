using System.Globalization;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;

namespace VelaCommerce.SeedGen;

/// <summary>
/// Builds the demo catalog through the real domain types and then projects it to DTOs.
/// <para>
/// Going through <see cref="Product"/>, <see cref="Product.AddVariant"/>, <see cref="Money"/>
/// and <see cref="StockItem"/> rather than straight to JSON is deliberate: it makes this
/// tool a compile-time and run-time check that the domain API is actually usable to build a
/// catalog, and it means the domain's own guards (duplicate SKU, negative price, negative
/// stock) fire here rather than in production.
/// </para>
/// <para>
/// Determinism is a hard requirement. Every random choice comes from one
/// <see cref="Random"/> seeded with <see cref="RandomSeed"/> — the seeded constructor keeps
/// .NET's legacy algorithm for exactly this compatibility reason, so the sequence is stable
/// across runs, machines and framework versions. No <c>Guid.NewGuid</c>, no clock. The
/// identifiers on the entities built here ARE fresh UUIDv7 values on every run, which is
/// precisely why they never reach the output file.
/// </para>
/// </summary>
internal static class CatalogGenerator
{
    public const int RandomSeed = 20260214;
    public const int SchemaVersion = 1;

    private const int TargetProductCount = 288; // 36 per family, so facets are evenly populated
    private const int MaxNamingAttempts = 64;
    // internal because the client snapshot factors this prefix out of 691 image paths and
    // publishes it once as its imageBase; both files must agree on where the pictures live.
    internal const string ImageRoot = "/images/catalog";

    public static SeedCatalog Generate()
    {
        var rng = new Random(RandomSeed);
        var families = CatalogBlueprint.Families;
        var slugsTaken = new HashSet<string>(StringComparer.Ordinal);
        var skusTaken = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<PendingProduct>(TargetProductCount);

        for (var i = 0; i < TargetProductCount; i++)
        {
            // Round-robin over families rather than a random pick, so every category ends up
            // with enough rows to make faceted search look like a real store.
            var family = families[i % families.Count];
            pending.Add(BuildProduct(rng, family, ordinal: i + 1, slugsTaken, skusTaken));
        }

        var lastUnit = DesignateLastUnitDemo(rng, pending);

        var products = pending.Select(ToDto).ToArray();
        var variantCount = pending.Sum(p => p.Variants.Count);
        var totalStock = pending.Sum(p => p.Variants.Sum(v => v.OnHand));

        var metadata = new SeedMetadata(
            Generator: "VelaCommerce.SeedGen",
            SchemaVersion: SchemaVersion,
            RandomSeed: RandomSeed,
            Currency: Money.DefaultCurrency,
            ProductCount: products.Length,
            VariantCount: variantCount,
            TotalStockUnits: totalStock,
            LastUnitDemoSku: lastUnit.Variant.Sku);

        return new SeedCatalog(metadata, BuildAttribution(pending), products);
    }

    private static PendingProduct BuildProduct(
        Random rng,
        ProductFamily family,
        int ordinal,
        HashSet<string> slugsTaken,
        HashSet<string> skusTaken)
    {
        var (name, slug, material, type) = ComposeName(rng, family, slugsTaken);

        var product = new Product(slug, name, ComposeDescription(rng, family, material, type), family.Category);
        ApplyAttributes(rng, product, family, material);

        // Whole-dollar draw with a charm ending applied afterwards, so no floating point is
        // involved anywhere on the way to Money's minor units.
        var basePrice = rng.Next(family.MinPriceDollars, family.MaxPriceDollars + 1) - 0.05m;

        var pending = new PendingProduct(product, family);
        foreach (var index in ChooseOptionIndexes(rng, family))
        {
            var option = family.Options[index];
            var sku = $"VC-{family.Code}-{ordinal.ToString("D4", CultureInfo.InvariantCulture)}-{option.Code}";

            if (!skusTaken.Add(sku))
                throw new InvalidOperationException($"SKU '{sku}' was generated twice; the SKU scheme is no longer unique.");

            // Stepping by the option's own index (not its position in the chosen subset)
            // keeps a 30 m line dearer than a 15 m one even when 20 m was not stocked.
            var price = Money.FromDecimal(basePrice + index * family.VariantStepDollars);

            // Foldered by category so the attribution manifest can point at a real glob, and
            // so a photo shoot for one department drops into one directory.
            var image = $"{ImageRoot}/{family.Category}/{slug}-{Slug.From(option.Label)}.webp";

            var variant = product.AddVariant(sku, option.Label, price, image);
            pending.Variants.Add(new PendingVariant(variant) { OnHand = RollStock(rng) });
        }

        return pending;
    }

    /// <summary>
    /// Composes a name from model x material x type, retrying on a slug collision. Roughly
    /// three names in ten drop the material, which stops the catalog reading like a template.
    /// </summary>
    private static (string Name, string Slug, string Material, string Type) ComposeName(
        Random rng,
        ProductFamily family,
        HashSet<string> slugsTaken)
    {
        for (var attempt = 0; attempt < MaxNamingAttempts; attempt++)
        {
            var model = Pick(rng, CatalogBlueprint.ModelNames);
            var type = Pick(rng, family.Types);
            var material = Pick(rng, family.Materials);
            var name = rng.Next(10) < 7 ? $"{model} {material} {type}" : $"{model} {type}";
            var slug = Slug.From(name);

            if (slugsTaken.Add(slug))
                return (name, slug, material, type);
        }

        throw new InvalidOperationException(
            $"No unique name found for '{family.Category}' in {MaxNamingAttempts} attempts; widen the word banks.");
    }

    private static string ComposeDescription(Random rng, ProductFamily family, string material, string type) =>
        $"{material} {type.ToLowerInvariant()}, built for {Pick(rng, CatalogBlueprint.UseCases)}. " +
        $"{Pick(rng, family.Features)} {Pick(rng, CatalogBlueprint.Closers)}";

    /// <summary>
    /// Always sets material and origin, then adds one to four facets drawn from the family's
    /// pool plus the shared one, landing every product between three and six attributes.
    /// </summary>
    private static void ApplyAttributes(Random rng, Product product, ProductFamily family, string material)
    {
        product.Attributes["material"] = material;
        product.Attributes["origin"] = Pick(rng, CatalogBlueprint.Origins);

        var pool = family.Attributes.Concat(CatalogBlueprint.SharedAttributes).ToArray();
        ShuffleInPlace(rng, pool);

        var extras = rng.Next(1, 5);
        for (var i = 0; i < extras; i++)
            product.Attributes[pool[i].Key] = Pick(rng, pool[i].Values);
    }

    /// <summary>
    /// Picks one to four positions on the variant axis, returned in axis order so a size
    /// picker reads Small, Large, XX-Large rather than in draw order.
    /// </summary>
    private static int[] ChooseOptionIndexes(Random rng, ProductFamily family)
    {
        var indexes = new int[family.Options.Count];
        for (var i = 0; i < indexes.Length; i++) indexes[i] = i;

        ShuffleInPlace(rng, indexes);

        var wanted = Math.Min(indexes.Length, rng.Next(1, 5));
        var chosen = indexes[..wanted];
        Array.Sort(chosen);
        return chosen;
    }

    /// <summary>
    /// Stock never lands on 1 by chance: about one product in sixteen is sold out (so the
    /// out-of-stock state is visible in the grid), some sit at 2-5 for a low-stock badge, and
    /// the rest are comfortably stocked. Exactly one variant is set to 1 afterwards.
    /// </summary>
    private static int RollStock(Random rng)
    {
        var roll = rng.Next(100);
        if (roll < 6) return 0;
        if (roll < 20) return rng.Next(2, 6);
        return rng.Next(8, 240);
    }

    /// <summary>
    /// Chooses the one variant seeded at a single unit — the item the Demo Lab race is run
    /// against. Restricted to multi-variant products so the reviewer also sees the variant
    /// picker switch between an available size and the contended one.
    /// </summary>
    private static PendingVariant DesignateLastUnitDemo(Random rng, List<PendingProduct> products)
    {
        var candidates = products.Where(p => p.Variants.Count >= 2).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("No multi-variant product available to host the last-unit demo.");

        var product = candidates[rng.Next(candidates.Length)];
        var variant = product.Variants[rng.Next(product.Variants.Count)];

        variant.OnHand = 1;
        variant.IsLastUnitDemo = true;
        return variant;
    }

    private static SeedProduct ToDto(PendingProduct pending)
    {
        var product = pending.Product;

        // Sorted so the committed file is byte-stable regardless of how Dictionary happens to
        // order its buckets; the domain's Attributes bag is deliberately unordered.
        var attributes = new SortedDictionary<string, string>(product.Attributes, StringComparer.Ordinal);

        var variants = pending.Variants.Select(v =>
        {
            // Constructing the StockItem is the point: its constructor is what rejects a
            // negative opening quantity, so a bad roll fails here instead of at import.
            var stock = new StockItem(v.Variant.Id, v.OnHand);

            return new SeedVariant(
                Sku: v.Variant.Sku,
                Name: v.Variant.Name,
                Price: ToDto(v.Variant.Price),
                ImageUrl: v.Variant.ImageUrl ?? string.Empty,
                StockOnHand: stock.OnHand,
                IsLastUnitDemo: v.IsLastUnitDemo);
        }).ToArray();

        return new SeedProduct(
            Slug: product.Slug,
            Name: product.Name,
            Description: product.Description,
            Category: product.Category,
            VariantAxis: pending.Family.VariantAxis,
            Attributes: attributes,
            FromPrice: product.FromPrice is { } from ? ToDto(from) : null,
            Variants: variants);
    }

    private static MoneyDto ToDto(Money money) => new(money.Amount, money.Currency);

    /// <summary>
    /// The image manifest. Filenames are generated; the pictures are not. Every row states
    /// the obligation that has to be discharged before the storefront ships.
    /// </summary>
    private static AttributionEntry[] BuildAttribution(List<PendingProduct> products)
    {
        const string license = "Required: CC0 1.0, CC BY 4.0, or Unsplash License. Record the photographer and source URL here.";

        var total = products.Sum(p => p.Variants.Count);

        var perCategory = CatalogBlueprint.Families.Select(family => new AttributionEntry(
            Asset: $"{ImageRoot}/{family.Category}/*.webp",
            Category: family.Category,
            ImageCount: products.Where(p => p.Family.Category == family.Category).Sum(p => p.Variants.Count),
            Source: "Placeholder - not yet sourced",
            License: license,
            Note: $"Needs images of {family.ImageSubject}."));

        return
        [
            new AttributionEntry(
                Asset: $"{ImageRoot}/**/*.webp",
                Category: "all",
                ImageCount: total,
                Source: "Placeholder - not yet sourced",
                License: license,
                Note: "SeedGen generates image FILENAMES only. Nothing under this path is downloaded or "
                    + "generated, and no image may be committed until it is sourced under a permissive "
                    + "licence and credited in this manifest."),
            .. perCategory,
        ];
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    /// <summary>
    /// Fisher-Yates written out rather than <c>Random.Shuffle</c>, so the committed seed file
    /// cannot change underneath us if that implementation is ever retuned.
    /// </summary>
    private static void ShuffleInPlace<T>(Random rng, T[] items)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Domain objects plus the seed-only facts (stock, demo flag) they do not carry.</summary>
    private sealed class PendingProduct(Product product, ProductFamily family)
    {
        public Product Product { get; } = product;
        public ProductFamily Family { get; } = family;
        public List<PendingVariant> Variants { get; } = [];
    }

    private sealed class PendingVariant(ProductVariant variant)
    {
        public ProductVariant Variant { get; } = variant;

        /// <summary>Mutable because the last-unit demo is chosen after all stock is rolled.</summary>
        public int OnHand { get; set; }

        public bool IsLastUnitDemo { get; set; }
    }
}
