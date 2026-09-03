using System.Text;

namespace VelaCommerce.SeedGen;

/// <summary>
/// Projects the generated seed catalog into the client snapshot the storefront ships as a
/// static file.
/// <para>
/// This is a projection, not a second generator, and that is the point: the storefront's copy
/// of the catalog cannot drift from the importer's copy, because there is only one catalog and
/// one random seed behind both. Nothing here reads a clock or a fresh identifier, so the
/// snapshot is byte-identical between runs like everything else in this tool.
/// </para>
/// </summary>
internal static class CatalogSnapshotBuilder
{
    /// <summary>
    /// Words that stay lower-case inside a department label, so "bags-and-storage" reads as
    /// "Bags and Storage" rather than shouting "Bags And Storage" in the filter rail.
    /// </summary>
    private static readonly string[] LowercaseJoiners = ["and", "or", "of", "the", "for"];

    public static CatalogSnapshot From(SeedCatalog catalog)
    {
        var metadata = catalog.Metadata;
        var currency = metadata.Currency;

        var products = catalog.Products.Select(product => ToSnapshot(product, currency)).ToArray();
        if (products.Length == 0)
            throw new InvalidOperationException("The catalog is empty; there is no snapshot to write.");

        var prices = products.SelectMany(product => product.Variants).Select(variant => variant.Price).ToArray();
        if (prices.Length == 0)
            throw new InvalidOperationException("No product carries a variant, so the snapshot has no price range to publish.");

        // Cheap self-check: the snapshot and the seed file are read as two views of one
        // catalog, so a divergence in the counts means one of them is lying about the store.
        if (products.Length != metadata.ProductCount || prices.Length != metadata.VariantCount)
            throw new InvalidOperationException(
                $"Snapshot counts ({products.Length} products, {prices.Length} variants) disagree with the seed metadata "
                + $"({metadata.ProductCount} products, {metadata.VariantCount} variants).");

        var categories = products
            .GroupBy(product => product.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(ToCategory)
            .ToArray();

        return new CatalogSnapshot(
            SchemaVersion: CatalogGenerator.SchemaVersion,
            Generator: metadata.Generator,
            RandomSeed: metadata.RandomSeed,
            Currency: currency,
            ImageBase: CatalogGenerator.ImageRoot,
            ProductCount: products.Length,
            VariantCount: prices.Length,
            MinPrice: prices.Min(),
            MaxPrice: prices.Max(),
            Categories: categories,
            Products: products);
    }

    /// <summary>
    /// Lower-cases a string and reduces every run of non-alphanumerics to a single space.
    /// <para>
    /// The storefront must put the shopper's query through this same function before testing
    /// it against a product's <c>search</c> field, or a hyphen typed on one side and not the
    /// other quietly returns nothing.
    /// </para>
    /// </summary>
    public static string Normalise(string value)
    {
        var builder = new StringBuilder(value.Length);
        AppendNormalised(builder, value);
        return builder.ToString().TrimEnd(' ');
    }

    private static SnapshotProduct ToSnapshot(SeedProduct product, string currency)
    {
        // Sorted ordinally so the file is byte-stable no matter how the source bag enumerates,
        // and so the spec table on the product page has one predictable reading order.
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in product.Attributes)
            attributes[pair.Key] = pair.Value;

        var variants = product.Variants
            .Select(variant => new SnapshotVariant(
                Sku: variant.Sku,
                Name: variant.Name,
                Price: Amount(variant.Price, currency, variant.Sku),
                Image: RelativeImage(variant.ImageUrl, variant.Sku)))
            .ToArray();

        return new SnapshotProduct(
            Slug: product.Slug,
            Name: product.Name,
            Description: product.Description,
            Category: product.Category,
            VariantAxis: NullIfBlank(product.VariantAxis),
            From: product.FromPrice is { } fromPrice ? Amount(fromPrice, currency, product.Slug) : null,
            Attributes: attributes.Count > 0 ? attributes : null,
            Search: BuildSearchText(product, attributes),
            Variants: variants);
    }

    private static SnapshotCategory ToCategory(IGrouping<string, SnapshotProduct> group)
    {
        var prices = group.SelectMany(product => product.Variants).Select(variant => variant.Price).ToArray();

        var axes = group
            .Select(product => product.VariantAxis)
            .Where(axis => axis is not null)
            .Select(axis => axis!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var attributeKeys = group
            .Where(product => product.Attributes is not null)
            .SelectMany(product => product.Attributes!.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new SnapshotCategory(
            Slug: group.Key,
            Name: DisplayName(group.Key),
            // One axis per department today. Left null rather than guessed if that ever stops
            // being true, so the UI falls back to the per-product axis instead of mislabelling
            // a picker for every product in the department.
            Axis: axes.Length == 1 ? axes[0] : null,
            ProductCount: group.Count(),
            MinPrice: prices.Length == 0 ? 0 : prices.Min(),
            MaxPrice: prices.Length == 0 ? 0 : prices.Max(),
            AttributeKeys: attributeKeys);
    }

    /// <summary>
    /// Builds the substring-search haystack: name, description, category and attribute values,
    /// normalised once here so the client does not redo it for 288 products on every keystroke.
    /// Attribute keys are left out — nobody searches for "breaking-load-kg", they search for
    /// "stainless" or "Bristol".
    /// </summary>
    private static string BuildSearchText(SeedProduct product, SortedDictionary<string, string> attributes)
    {
        var builder = new StringBuilder(product.Name.Length + product.Description.Length + 128);

        AppendNormalised(builder, product.Name);
        AppendNormalised(builder, product.Description);
        AppendNormalised(builder, product.Category);
        foreach (var pair in attributes)
            AppendNormalised(builder, pair.Value);

        return builder.ToString().TrimEnd(' ');
    }

    /// <summary>
    /// Appends one fragment, then a single trailing space so the next fragment cannot fuse
    /// with it and invent a word that matches nothing.
    /// </summary>
    private static void AppendNormalised(StringBuilder builder, string value)
    {
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }

        if (builder.Length > 0 && builder[^1] != ' ')
            builder.Append(' ');
    }

    /// <summary>
    /// Strips the shared image root, which the snapshot names once. Throws rather than falling
    /// back to the absolute path: a client that joins <c>imageBase</c> with a value that is
    /// already absolute produces a 404 for every card, and a loud build beats a silent one.
    /// </summary>
    private static string? RelativeImage(string imageUrl, string sku)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        const string prefix = CatalogGenerator.ImageRoot + "/";

        if (!imageUrl.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Variant '{sku}' points at '{imageUrl}', which is not under '{CatalogGenerator.ImageRoot}'. "
                + "The snapshot stores image paths relative to that root; move the asset or widen the contract.");

        return imageUrl[prefix.Length..];
    }

    /// <summary>
    /// The snapshot names its currency once at the root, so a second currency in the catalog
    /// would silently reprice the store. Refuse instead.
    /// </summary>
    private static long Amount(MoneyDto money, string currency, string context)
    {
        if (!string.Equals(money.Currency, currency, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{context}' is priced in {money.Currency}, but the snapshot declares {currency} once at the root. "
                + "A multi-currency catalog needs a per-price currency, which this format deliberately does not carry.");

        return money.AmountMinorUnits;
    }

    /// <summary>Turns a category slug into a shelf label: "rope-and-rigging" to "Rope and Rigging".</summary>
    private static string DisplayName(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(slug.Length);

        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                builder.Append(' ');

            var word = words[i];

            if (i > 0 && Array.IndexOf(LowercaseJoiners, word) >= 0)
                builder.Append(word);
            else
                builder.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        return builder.ToString();
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
