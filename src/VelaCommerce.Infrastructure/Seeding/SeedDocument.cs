using System.Text.Json.Serialization;

namespace VelaCommerce.Infrastructure.Seeding;

/// <summary>
/// The on-disk shape of catalog.seed.json, produced by VelaCommerce.SeedGen.
/// Kept as plain DTOs so the seed file stays a data contract rather than a
/// serialized dump of the domain model.
/// </summary>
public sealed record SeedDocument
{
    [JsonPropertyName("metadata")] public SeedMetadata Metadata { get; init; } = new();
    [JsonPropertyName("products")] public IReadOnlyList<SeedProduct> Products { get; init; } = [];
}

public sealed record SeedMetadata
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("randomSeed")] public int RandomSeed { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";
    [JsonPropertyName("productCount")] public int ProductCount { get; init; }
    [JsonPropertyName("variantCount")] public int VariantCount { get; init; }
    [JsonPropertyName("lastUnitDemoSku")] public string? LastUnitDemoSku { get; init; }
}

public sealed record SeedProduct
{
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("attributes")] public Dictionary<string, string> Attributes { get; init; } = [];
    [JsonPropertyName("variants")] public IReadOnlyList<SeedVariant> Variants { get; init; } = [];
}

public sealed record SeedVariant
{
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("price")] public SeedMoney Price { get; init; } = new();
    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; init; }
    [JsonPropertyName("stockOnHand")] public int StockOnHand { get; init; }
}

public sealed record SeedMoney
{
    [JsonPropertyName("amountMinorUnits")] public long AmountMinorUnits { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";
}
