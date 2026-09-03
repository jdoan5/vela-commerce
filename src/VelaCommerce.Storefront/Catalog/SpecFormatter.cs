namespace VelaCommerce.Storefront.Catalog;

/// <summary>One spec, split so the UI can typeset the number and its unit differently.</summary>
public sealed record SpecReading(string Key, string Label, string Value, string? Unit, bool IsNumeric);

/// <summary>
/// Turns raw snapshot attribute keys into readable, unit-aware readings.
/// <para>
/// The keys are kebab-case and carry their unit in the name — <c>breaking-load-kg</c>,
/// <c>output-lumens</c>, <c>capacity-litres</c>. Printing them as-is wastes the best thing
/// about this catalog: it is full of breaking loads, lumens, IP ratings and chart scales,
/// which want to look like instrument readings. Splitting label from unit here lets the
/// stylesheet set the number in tabular figures and the unit in small caps, everywhere,
/// without every component re-deriving the split.
/// </para>
/// </summary>
public static class SpecFormatter
{
    /// <summary>
    /// Key suffixes that name a unit, mapped to how the unit should be printed. Longest
    /// suffix wins, so <c>pin-diameter-mm</c> and <c>diameter-mm</c> both resolve.
    /// </summary>
    private static readonly (string Suffix, string Unit)[] UnitSuffixes =
    [
        ("-kg", "kg"),
        ("-mm", "mm"),
        ("-lumens", "lm"),
        ("-litres", "L"),
        ("-amps", "A"),
    ];

    /// <summary>
    /// Keys whose display label is not simply the kebab-case key spelled out. Kept small on
    /// purpose: the generator's naming is good, and a big override table would rot.
    /// </summary>
    private static readonly Dictionary<string, string> LabelOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ip-rating"] = "IP rating",
        ["draw-amps"] = "Current draw",
        ["output-lumens"] = "Output",
        ["capacity-litres"] = "Capacity",
        ["waterproof-rating"] = "Water column",
        ["breathability"] = "Breathability",
        ["elongation"] = "Elongation",
        ["graduation"] = "Graduation",
        ["pages"] = "Extent",
    };

    /// <summary>
    /// Which spec earns the one line a product card can spare, best first. Every product in
    /// the snapshot carries <c>material</c>, so this list always resolves and a card never
    /// changes height because a spec was missing.
    /// </summary>
    private static readonly string[] HeadlinePriority =
    [
        "breaking-load-kg",
        "working-load-kg",
        "output-lumens",
        "capacity-litres",
        "scale",
        "waterproof-rating",
        "pin-diameter-mm",
        "diameter-mm",
        "length-mm",
        "pages",
        "graduation",
        "accuracy",
        "ip-rating",
        "steel",
        "breathability",
        "draw-amps",
        "power",
        "material",
    ];

    /// <summary>The unit a key implies, or null when the value is prose rather than a measurement.</summary>
    public static string? Unit(string key)
    {
        foreach (var (suffix, unit) in UnitSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return unit;
        }

        return null;
    }

    /// <summary>The human label for a key: overrides first, otherwise the kebab-case key unpacked and sentence-cased.</summary>
    public static string Label(string key)
    {
        if (LabelOverrides.TryGetValue(key, out var over))
            return over;

        var withoutUnit = key;
        foreach (var (suffix, _) in UnitSuffixes)
        {
            if (withoutUnit.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                withoutUnit = withoutUnit[..^suffix.Length];
                break;
            }
        }

        return SentenceCaseFromSlug(withoutUnit);
    }

    /// <summary>Builds a display-ready reading for one attribute of a product.</summary>
    public static SpecReading Read(string key, string value) =>
        new(key, Label(key), value, Unit(key), LooksNumeric(value));

    /// <summary>Every attribute of a product as readings, in the snapshot's own key order.</summary>
    public static IReadOnlyList<SpecReading> ReadAll(CatalogProduct product) =>
        [.. product.Attributes.Select(pair => Read(pair.Key, pair.Value))];

    /// <summary>
    /// The single most interesting spec on a product, for the one line a card can spare.
    /// Returns null only if a product somehow carries no attributes at all.
    /// </summary>
    public static SpecReading? Headline(CatalogProduct product)
    {
        foreach (var key in HeadlinePriority)
        {
            if (product.Attributes.TryGetValue(key, out var value))
                return Read(key, value);
        }

        foreach (var pair in product.Attributes)
            return Read(pair.Key, pair.Value);

        return null;
    }

    /// <summary>
    /// True when a value is a measurement rather than prose, so the stylesheet can set it in
    /// tabular figures. Grouped numbers such as "3,300" and ratios such as "1:40,000" count;
    /// "Passivated, 500 h salt spray" does not.
    /// </summary>
    public static bool LooksNumeric(string value)
    {
        if (value.Length == 0)
            return false;

        var sawDigit = false;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                sawDigit = true;
                continue;
            }

            if (c is ',' or '.' or ':' or '/' or '-' or ' ')
                continue;

            return false;
        }

        return sawDigit;
    }

    /// <summary>
    /// "rope-and-rigging" to "Rope &amp; Rigging", "foul-weather-gear" to "Foul Weather Gear".
    /// Shared with category naming so a slug reads the same wherever it surfaces.
    /// </summary>
    public static string TitleCaseFromSlug(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(static word =>
            word.Equals("and", StringComparison.OrdinalIgnoreCase)
                ? "&"
                : Capitalise(word)));
    }

    /// <summary>Like <see cref="TitleCaseFromSlug"/> but only the first word is capitalised, for spec labels.</summary>
    private static string SentenceCaseFromSlug(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return slug;

        return string.Join(' ', words.Select(static (word, index) =>
            index == 0 ? Capitalise(word) : word.ToLowerInvariant()));
    }

    private static string Capitalise(string word) =>
        word.Length == 0
            ? word
            : $"{char.ToUpperInvariant(word[0])}{word[1..]}";
}
