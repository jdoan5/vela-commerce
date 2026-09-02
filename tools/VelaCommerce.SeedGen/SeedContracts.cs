using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaCommerce.SeedGen;

/// <summary>
/// The root of the seed file: a flat product list plus the provenance the importer and a
/// reviewer both need. Domain entities are never serialised directly — they carry
/// UUIDv7 identifiers minted in their constructors, which would change on every run and
/// make the committed file churn. The importer assigns identity.
/// </summary>
internal sealed record SeedCatalog(
    SeedMetadata Metadata,
    IReadOnlyList<AttributionEntry> Attribution,
    IReadOnlyList<SeedProduct> Products);

/// <summary>
/// Provenance and self-check totals. Deliberately carries no generated-at timestamp:
/// the file must be byte-identical between runs so a re-generation shows up in review as
/// a real catalog change or as no diff at all.
/// </summary>
internal sealed record SeedMetadata(
    string Generator,
    int SchemaVersion,
    int RandomSeed,
    string Currency,
    int ProductCount,
    int VariantCount,
    int TotalStockUnits,
    string LastUnitDemoSku);

/// <summary>An amount in minor units, matching <c>Money</c> so the importer needs no rounding.</summary>
internal sealed record MoneyDto(long AmountMinorUnits, string Currency);

/// <summary>
/// One catalog product. <c>FromPrice</c> is precomputed from the domain's own
/// <c>Product.FromPrice</c> so a storefront card can render "from $X" without loading variants.
/// </summary>
internal sealed record SeedProduct(
    string Slug,
    string Name,
    string Description,
    string Category,
    string VariantAxis,
    IReadOnlyDictionary<string, string> Attributes,
    MoneyDto? FromPrice,
    IReadOnlyList<SeedVariant> Variants);

/// <summary>
/// One buyable SKU plus the stock the importer should open it with.
/// <para>
/// <c>IsLastUnitDemo</c> marks the single variant seeded at one unit, the one a reviewer
/// races against itself in two tabs. It is omitted when false, so the flag is greppable
/// and 700-odd variants do not each carry a line of noise.
/// </para>
/// </summary>
internal sealed record SeedVariant(
    string Sku,
    string Name,
    MoneyDto Price,
    string ImageUrl,
    int StockOnHand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool IsLastUnitDemo);

/// <summary>
/// An ATTRIBUTION.md row, carried in the seed file so the licence obligation travels with
/// the filenames that create it. No image is downloaded or generated here.
/// </summary>
internal sealed record AttributionEntry(
    string Asset,
    string Category,
    int ImageCount,
    string Source,
    string License,
    string Note);

/// <summary>
/// Serialisation settings shared by the generator and any future importer test.
/// camelCase for the wire; indented because this file is committed and read in review.
/// </summary>
internal static class SeedJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
