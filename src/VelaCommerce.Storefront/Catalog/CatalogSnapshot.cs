using System.Text.Json.Serialization;

namespace VelaCommerce.Storefront.Catalog;

/// <summary>
/// The whole shop, as one static file.
/// <para>
/// This mirrors the snapshot <c>VelaCommerce.SeedGen</c> emits, which is a projection of the
/// seed rather than a copy of it. The differences are deliberate and each one costs bytes on
/// the first-paint path: property names are abbreviated, prices are bare minor-unit integers
/// with the currency named once here, and stock is absent entirely because it is live state
/// that would be wrong the moment somebody bought something.
/// </para>
/// <para>
/// The storefront's first paint may not depend on anything that can be asleep, so this file
/// is fetched from the app's own origin and the API is never called to render the catalog.
/// </para>
/// </summary>
public sealed record CatalogSnapshot
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("generator")] public string Generator { get; init; } = "";
    [JsonPropertyName("randomSeed")] public int RandomSeed { get; init; }

    /// <summary>Named once for the whole catalog, rather than repeated 979 times.</summary>
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";

    /// <summary>Root that every variant image path is relative to, so the CDN can move.</summary>
    [JsonPropertyName("imageBase")] public string ImageBase { get; init; } = "";

    [JsonPropertyName("productCount")] public int ProductCount { get; init; }
    [JsonPropertyName("variantCount")] public int VariantCount { get; init; }
    [JsonPropertyName("minPrice")] public long MinPrice { get; init; }
    [JsonPropertyName("maxPrice")] public long MaxPrice { get; init; }

    [JsonPropertyName("categories")] public IReadOnlyList<SnapshotCategory> Categories { get; init; } = [];
    [JsonPropertyName("products")] public IReadOnlyList<CatalogProduct> Products { get; init; } = [];
}

/// <summary>
/// A shelf, precomputed by the generator so the filter UI never walks 288 products to
/// discover what the categories are or which attributes they carry.
/// </summary>
public sealed record SnapshotCategory
{
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("axis")] public string VariantAxis { get; init; } = "";
    [JsonPropertyName("count")] public int ProductCount { get; init; }
    [JsonPropertyName("minPrice")] public long MinPrice { get; init; }
    [JsonPropertyName("maxPrice")] public long MaxPrice { get; init; }
    [JsonPropertyName("attrs")] public IReadOnlyList<string> AttributeKeys { get; init; } = [];
}

/// <summary>
/// An amount in minor units, matching the domain's <c>Money</c>. Never a <c>double</c> or a
/// <c>float</c>: prices are counted, not measured. Rendering goes through
/// <see cref="MoneyFormatter"/> so the decimal point is placed in exactly one place.
/// </summary>
public sealed record CatalogMoney(long AmountMinorUnits, string Currency);

/// <summary>
/// One catalog product.
/// <para>
/// The JSON carries a bare integer for the price and no currency. <see cref="Currency"/> is
/// stamped on by <see cref="CatalogService"/> after deserialisation from the snapshot's single
/// currency, which lets <see cref="FromPrice"/> hand components the same
/// <see cref="CatalogMoney"/> they would get from any other source.
/// </para>
/// </summary>
public sealed record CatalogProduct
{
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("desc")] public string Description { get; init; } = "";
    [JsonPropertyName("cat")] public string Category { get; init; } = "";
    [JsonPropertyName("axis")] public string VariantAxis { get; init; } = "";

    /// <summary>Cheapest variant, in minor units. Null only if a product had no priced variant.</summary>
    [JsonPropertyName("from")] public long? FromPriceMinorUnits { get; init; }

    [JsonPropertyName("attrs")] public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Name, description, category and attribute values, lowercased and joined by the
    /// generator. Search is then one substring test per product instead of re-normalising
    /// four fields on every keystroke.
    /// </summary>
    [JsonPropertyName("search")] public string SearchBlob { get; init; } = "";

    [JsonPropertyName("vars")] public IReadOnlyList<CatalogVariant> Variants { get; init; } = [];

    /// <summary>Stamped on after load; not present in the JSON.</summary>
    [JsonIgnore] public string Currency { get; init; } = "USD";

    [JsonIgnore]
    public CatalogMoney? FromPrice =>
        FromPriceMinorUnits is { } minorUnits ? new CatalogMoney(minorUnits, Currency) : null;
}

/// <summary>
/// One buyable SKU.
/// <para>
/// <see cref="ImagePath"/> is a filename the generator invented; nothing is downloaded or
/// committed under that path, so the storefront must never point an <c>img</c> at it. The
/// grid draws a deterministic motif from the slug instead — see <see cref="ProductMotif"/>.
/// </para>
/// <para>
/// There is no stock here on purpose. Stock is live state, and a number baked into a CDN file
/// is wrong the moment someone buys something; the storefront would then have to choose which
/// of two truths to believe. Availability comes from the API, after first paint.
/// </para>
/// </summary>
public sealed record CatalogVariant
{
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("price")] public long PriceMinorUnits { get; init; }
    [JsonPropertyName("img")] public string ImagePath { get; init; } = "";

    /// <summary>Stamped on after load; not present in the JSON.</summary>
    [JsonIgnore] public string Currency { get; init; } = "USD";

    [JsonIgnore] public CatalogMoney Price => new(PriceMinorUnits, Currency);
}

/// <summary>
/// Source-generated JSON metadata for the snapshot.
/// <para>
/// Reflection-based serialisation works in the browser but produces trim warnings on publish
/// and drags the reflection stack into the download. The generator emits the readers at
/// compile time instead, which keeps a Release publish warning-free and the payload smaller —
/// and the payload is the thing a first paint waits on.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CatalogSnapshot))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
