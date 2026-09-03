using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaCommerce.SeedGen;

/// <summary>
/// The client catalog snapshot: everything the storefront needs to browse, search, filter
/// and sort the catalog with the API and database switched off entirely.
/// <para>
/// It is a projection of <see cref="SeedCatalog"/> rather than a second generator, so the
/// file the shopper reads and the file the importer loads cannot describe different stores.
/// </para>
/// <para>
/// It deliberately carries no stock. Stock is live state; baking it into a file that sits on
/// a CDN would ship a number that is wrong the moment someone buys something, and the
/// storefront would then have to decide which of two truths to believe.
/// </para>
/// <para>
/// Prices are bare minor-unit integers because the whole catalog is one currency, named once
/// in <see cref="Currency"/>. Repeating <c>{"amountMinorUnits":..,"currency":"USD"}</c> 979
/// times would cost more bytes than the product descriptions do, on the first-paint path.
/// </para>
/// </summary>
/// <param name="ImageBase">
/// Root every variant's <c>img</c> is relative to, so the 691 image paths do not each repeat
/// it and the storefront can repoint them at a CDN without regenerating the file. Join with
/// a single <c>/</c>.
/// </param>
/// <param name="MinPrice">Cheapest variant price in the catalog, in minor units; the filter UI's range floor.</param>
/// <param name="MaxPrice">Dearest variant price in the catalog, in minor units; the filter UI's range ceiling.</param>
internal sealed record CatalogSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("generator")] string Generator,
    [property: JsonPropertyName("randomSeed")] int RandomSeed,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("imageBase")] string ImageBase,
    [property: JsonPropertyName("productCount")] int ProductCount,
    [property: JsonPropertyName("variantCount")] int VariantCount,
    [property: JsonPropertyName("minPrice")] long MinPrice,
    [property: JsonPropertyName("maxPrice")] long MaxPrice,
    [property: JsonPropertyName("categories")] IReadOnlyList<SnapshotCategory> Categories,
    [property: JsonPropertyName("products")] IReadOnlyList<SnapshotProduct> Products);

/// <summary>
/// One department, precomputed so the filter UI never walks 288 products to discover what it
/// can offer: the count for the facet badge, the price band for a per-category range control,
/// the variant axis for the picker's label, and the attribute keys actually present here
/// (a chart scale filter has no business appearing under rope and rigging).
/// </summary>
/// <param name="Name">Display label derived from the slug, so the UI carries no lookup table of its own.</param>
internal sealed record SnapshotCategory(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("axis")] string? Axis,
    [property: JsonPropertyName("count")] int ProductCount,
    [property: JsonPropertyName("minPrice")] long MinPrice,
    [property: JsonPropertyName("maxPrice")] long MaxPrice,
    [property: JsonPropertyName("attrs")] IReadOnlyList<string> AttributeKeys);

/// <summary>
/// One product as the grid and the product page need it.
/// <para>
/// Property names are abbreviated because they repeat 288 times, but not down to single
/// letters: the file is committed and read in review, and <c>desc</c> costs three bytes more
/// than <c>d</c> while costing nothing to understand.
/// </para>
/// </summary>
/// <param name="From">
/// The "from" price in minor units, precomputed from the domain's own <c>Product.FromPrice</c>
/// so a card renders without touching the variant list. Omitted when a product has no variants.
/// </param>
/// <param name="Search">
/// Name, description, category and attribute values, lower-cased and stripped to
/// alphanumerics separated by single spaces. Search is then a substring test rather than the
/// same normalisation repeated across 288 products on every keystroke. Normalise the query
/// the same way — see <see cref="CatalogSnapshotBuilder.Normalise"/> — or "three-layer" will
/// not match the "three layer" stored here.
/// </param>
internal sealed record SnapshotProduct(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("desc")] string Description,
    [property: JsonPropertyName("cat")] string Category,
    [property: JsonPropertyName("axis")] string? VariantAxis,
    [property: JsonPropertyName("from")] long? From,
    [property: JsonPropertyName("attrs")] IReadOnlyDictionary<string, string>? Attributes,
    [property: JsonPropertyName("search")] string Search,
    [property: JsonPropertyName("vars")] IReadOnlyList<SnapshotVariant> Variants);

/// <summary>
/// One SKU as it appears in a picker: enough to label it, price it and illustrate it, and
/// nothing that goes stale. Stock lives behind the API, and is fetched only when a shopper
/// commits to something — never on first paint.
/// </summary>
/// <param name="Price">Minor units in the catalog currency named on the snapshot root.</param>
/// <param name="Image">Relative to the snapshot's <c>imageBase</c>. Omitted when unset.</param>
internal sealed record SnapshotVariant(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] long Price,
    [property: JsonPropertyName("img")] string? Image);

/// <summary>
/// Serialisation settings for the snapshot. Minified, unlike the seed file: this one is
/// downloaded by every visitor before anything renders, and nobody reads it in review —
/// they read the seed file it was projected from. Nulls are dropped so an absent attribute
/// bag or image costs nothing.
/// </summary>
internal static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
