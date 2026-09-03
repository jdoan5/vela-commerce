namespace VelaCommerce.Storefront.Catalog;

/// <summary>
/// The family of drawing a category gets. One per category, chosen because it is what that
/// shelf of the chandlery actually looks like: rope lays like rope, charts carry soundings,
/// deck hardware is a bolt circle.
/// </summary>
public enum MotifKind
{
    /// <summary>Concentric rings, a tick ring and an eight-point star. Navigation.</summary>
    CompassRose,

    /// <summary>Laid strands running on the diagonal. Rope and rigging.</summary>
    RopeLay,

    /// <summary>Nested isolines with sounding numbers. Charts and books.</summary>
    DepthContours,

    /// <summary>A bolt circle around a hex centre. Deck hardware.</summary>
    BoltCircle,

    /// <summary>Stacked chevrons, the way a weather map draws a front. Foul-weather gear.</summary>
    StormChevrons,

    /// <summary>A drop-point blade silhouette with its bevel and rivets. Knives and tools.</summary>
    BladeProfile,

    /// <summary>Fresnel steps inside a lens with beams leaving it. Lamps and lighting.</summary>
    LensRings,

    /// <summary>Interlaced canvas webbing with a stitched border. Bags and storage.</summary>
    CanvasWeave,
}

/// <summary>
/// The drawing instructions for one product's placeholder.
/// <para>
/// No catalog image exists — the seed generates filenames only, and none may be committed
/// until it is licensed and credited. A broken <c>img</c> would be worse than no image, and
/// eight identical grey boxes would be worse still, so every card draws itself: a motif
/// chosen by category and detailed by a hash of the slug. Deterministic, so a product looks
/// the same on every visit and every machine, and free, so the grid costs no requests.
/// </para>
/// </summary>
/// <param name="Kind">Which family of drawing to render.</param>
/// <param name="Seed">A stable hash of the product slug, the source of every varied number.</param>
/// <param name="ToneIndex">0-3. Selects one of four tonal variants within the category's palette.</param>
/// <param name="Rotation">Degrees of rotation applied to the motif's main figure.</param>
/// <param name="Density">3-8. How many strands, rings, chevrons or contours to draw.</param>
public sealed record MotifDesign(
    MotifKind Kind,
    uint Seed,
    int ToneIndex,
    double Rotation,
    int Density)
{
    /// <summary>The CSS modifier suffix for this motif, matching the <c>.vc-motif--*</c> classes in app.css.</summary>
    public string CssName => Kind switch
    {
        MotifKind.CompassRose => "compass",
        MotifKind.RopeLay => "rope",
        MotifKind.DepthContours => "contours",
        MotifKind.BoltCircle => "bolt",
        MotifKind.StormChevrons => "storm",
        MotifKind.BladeProfile => "blade",
        MotifKind.LensRings => "lens",
        _ => "weave",
    };

    /// <summary>
    /// A fresh deterministic stream for this design. A component calls it at the top of a
    /// render and pulls numbers in a fixed order, so the same product draws identically every
    /// time without the component having to hold state.
    /// </summary>
    public MotifRandom CreateRandom() => new(Seed);
}

/// <summary>
/// A tiny linear congruential generator, seeded from a product slug.
/// <para>
/// Deliberately not <see cref="Random"/>: this must produce the same picture in every
/// browser and every .NET version, and <see cref="Random"/>'s shared implementation offers
/// no such guarantee. The constants are Numerical Recipes'.
/// </para>
/// </summary>
public sealed class MotifRandom
{
    private uint _state;

    /// <summary>Starts a stream at the given seed.</summary>
    /// <param name="seed">A stable hash; zero is nudged off zero so the stream is not degenerate.</param>
    public MotifRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    /// <summary>The next value in [0, 1).</summary>
    public double NextDouble()
    {
        _state = unchecked((_state * 1664525u) + 1013904223u);
        return (_state >> 8) / 16777216.0;
    }

    /// <summary>The next value in [min, max).</summary>
    public double Next(double min, double max) => min + (NextDouble() * (max - min));

    /// <summary>The next integer in [min, max].</summary>
    public int NextInt(int min, int max) => min + (int)(NextDouble() * (max - min + 1));
}

/// <summary>
/// Maps a product to the picture it draws. Pure, allocation-light and deterministic.
/// </summary>
public static class ProductMotif
{
    /// <summary>
    /// Category slug to motif family. Anything unrecognised falls back to the weave, which is
    /// the most neutral of the eight, so a new category added to the seed still renders.
    /// </summary>
    private static readonly Dictionary<string, MotifKind> ByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["navigation"] = MotifKind.CompassRose,
        ["rope-and-rigging"] = MotifKind.RopeLay,
        ["charts-and-books"] = MotifKind.DepthContours,
        ["deck-hardware"] = MotifKind.BoltCircle,
        ["foul-weather-gear"] = MotifKind.StormChevrons,
        ["knives-and-tools"] = MotifKind.BladeProfile,
        ["lamps-and-lighting"] = MotifKind.LensRings,
        ["bags-and-storage"] = MotifKind.CanvasWeave,
    };

    /// <summary>Builds the design for a product.</summary>
    public static MotifDesign For(CatalogProduct product) => For(product.Slug, product.Category);

    /// <summary>Builds the design from a slug and category, for callers that have no product to hand.</summary>
    public static MotifDesign For(string slug, string category)
    {
        var seed = Hash(slug);
        var kind = ByCategory.TryGetValue(category, out var mapped) ? mapped : MotifKind.CanvasWeave;

        // Three independent slices of the hash, so tone, rotation and density vary
        // independently rather than moving together and banding the grid.
        var tone = (int)(seed % 4u);
        var rotation = ((seed >> 5) % 360u) / 10.0;
        var density = 3 + (int)((seed >> 13) % 6u);

        return new MotifDesign(kind, seed, tone, rotation, density);
    }

    /// <summary>
    /// FNV-1a over the slug's UTF-16 bytes. Chosen over <see cref="string.GetHashCode()"/>
    /// because that one is randomised per process: the grid would reshuffle its artwork on
    /// every page load, which is exactly the thing this design must not do.
    /// </summary>
    private static uint Hash(string value)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash = unchecked((hash ^ (byte)(c & 0xFF)) * prime);
            hash = unchecked((hash ^ (byte)(c >> 8)) * prime);
        }

        return hash;
    }
}
