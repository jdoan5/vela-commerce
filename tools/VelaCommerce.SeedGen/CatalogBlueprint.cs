namespace VelaCommerce.SeedGen;

/// <summary>One choice along a product's variant axis, e.g. size Large or length 30 m.</summary>
/// <param name="Label">Shown to the shopper and stored as the variant name.</param>
/// <param name="Code">Uppercase SKU segment; kept separate so "10 mm" can become "10MM".</param>
internal sealed record VariantOption(string Label, string Code);

/// <summary>
/// A facet key with the values it may take. Values are curated strings rather than
/// generated numbers so the catalog reads like a real store under faceted search.
/// </summary>
internal sealed record AttributeChoice(string Key, IReadOnlyList<string> Values);

/// <summary>
/// One department of the store. Word banks are scoped per family because the cross
/// product is what keeps names plausible: a merino snatch block or a bronze tide table
/// would give the demo away instantly.
/// </summary>
internal sealed record ProductFamily
{
    public required string Category { get; init; }

    /// <summary>Three-letter SKU segment, so a SKU says which department it came from.</summary>
    public required string Code { get; init; }

    /// <summary>What the variants of this family vary by; the storefront labels its picker with it.</summary>
    public required string VariantAxis { get; init; }

    public required IReadOnlyList<string> Types { get; init; }
    public required IReadOnlyList<string> Materials { get; init; }
    public required IReadOnlyList<VariantOption> Options { get; init; }
    public required IReadOnlyList<string> Features { get; init; }
    public required IReadOnlyList<AttributeChoice> Attributes { get; init; }

    /// <summary>Price band in whole dollars; the generator adds the charm ending.</summary>
    public required int MinPriceDollars { get; init; }

    public required int MaxPriceDollars { get; init; }

    /// <summary>Dollars added per step up the variant axis. Zero where the axis is not a size.</summary>
    public required int VariantStepDollars { get; init; }

    /// <summary>Plain-English subject line for the image attribution manifest.</summary>
    public required string ImageSubject { get; init; }
}

/// <summary>
/// The curated word banks behind Vela Commerce, a coastal and offshore outfitter.
/// <para>
/// Everything here is ASCII on purpose: the seed file is serialised with the default
/// (strict) JSON encoder, so a stray typographic dash or accent would land in the output
/// as a <c>\u</c> escape and make the committed file harder to read in review.
/// </para>
/// </summary>
internal static class CatalogBlueprint
{
    /// <summary>
    /// Model names double as the distinguishing word in a product name. Deliberately no
    /// "Halyard" or "Bosun" here, because both appear as product types and the pairing
    /// would read as "Bosun Stainless Bosun Multi-Tool".
    /// </summary>
    public static readonly IReadOnlyList<string> ModelNames =
    [
        "Fastnet", "Skerry", "Tramontane", "Levanter", "Quarterdeck", "Windward", "Leeward",
        "Bowline", "Harbour", "Shoal", "Fathom", "Beacon", "Ketch", "Yawl", "Cutter",
        "Foredeck", "Spinnaker", "Gunwale", "Transom", "Keelson", "Binnacle", "Lanyard",
        "Mizzen", "Cormorant", "Petrel", "Guillemot", "Kittiwake", "Hebrides", "Solent",
        "Kattegat", "Hatteras", "Fundy",
    ];

    public static readonly IReadOnlyList<string> UseCases =
    [
        "coastal passages",
        "week-long cruising",
        "night watches offshore",
        "harbour work and delivery trips",
        "the foredeck in a building breeze",
        "shorthanded sailing",
        "estuary and channel work",
        "winter deliveries",
        "club racing",
        "long weekends at anchor",
    ];

    public static readonly IReadOnlyList<string> Closers =
    [
        "Serviceable, with spares kept in stock.",
        "Backed by a five-year warranty.",
        "Sea-tested before it reached this catalog.",
        "Packed in recycled board, no plastic.",
        "Repairable rather than disposable.",
        "Made in small batches.",
    ];

    public static readonly IReadOnlyList<string> Origins =
    [
        "Portland, Maine",
        "Bristol, England",
        "Gothenburg, Sweden",
        "Halifax, Nova Scotia",
        "Auckland, New Zealand",
        "Lorient, France",
    ];

    /// <summary>Facets every department can carry, mixed into each family's own pool.</summary>
    public static readonly IReadOnlyList<AttributeChoice> SharedAttributes =
    [
        new("warranty", ["Five years", "Two years", "Lifetime against defects"]),
        new("care", ["Rinse with fresh water after use", "Wipe dry, store unrolled"]),
        new("packaging", ["Recycled board, plastic free"]),
    ];

    public static readonly IReadOnlyList<ProductFamily> Families =
    [
        new()
        {
            Category = "foul-weather-gear",
            Code = "FWG",
            VariantAxis = "Size",
            ImageSubject = "jackets and salopettes on a plain ground or worn on deck",
            MinPriceDollars = 129,
            MaxPriceDollars = 429,
            VariantStepDollars = 0,
            Types =
            [
                "Offshore Jacket", "Coastal Smock", "Bib Trouser", "Deck Salopette",
                "Spray Top", "Storm Cag", "Insulated Midlayer",
            ],
            Materials =
            [
                "Three-Layer", "Coated Ripstop", "Waxed Cotton", "Bonded Fleece",
                "Merino-Lined", "Recycled Sailcloth",
            ],
            Options =
            [
                new("Small", "S"), new("Medium", "M"), new("Large", "L"),
                new("X-Large", "XL"), new("XX-Large", "XXL"),
            ],
            Features =
            [
                "Seams are taped and the hood rolls away into the collar.",
                "Cut for movement, with articulation where the harness sits.",
                "Reinforced at the seat and knees, where gear actually fails.",
                "High-visibility hood panel and a light loop on the chest.",
            ],
            Attributes =
            [
                new("waterproof-rating", ["20,000 mm", "15,000 mm", "10,000 mm"]),
                new("breathability", ["15,000 g/m2/24h", "10,000 g/m2/24h"]),
                new("seams", ["Fully taped", "Critically taped"]),
                new("closure", ["Two-way waterproof zip", "Storm flap over a coil zip"]),
                new("fit", ["Regular", "Athletic", "Relaxed"]),
            ],
        },
        new()
        {
            Category = "deck-hardware",
            Code = "DCK",
            VariantAxis = "Size",
            ImageSubject = "blocks, cleats and shackles, macro on a neutral ground",
            MinPriceDollars = 24,
            MaxPriceDollars = 189,
            VariantStepDollars = 18,
            Types =
            [
                "Snatch Block", "Cam Cleat", "Bow Shackle", "Winch Handle",
                "Turnbuckle", "Padeye", "Fairlead", "Swivel Block",
            ],
            Materials =
            [
                "Bronze", "Forged Stainless", "Anodised Aluminium",
                "Nickel-Plated Brass", "Titanium", "Composite",
            ],
            Options =
            [
                new("8 mm", "08MM"), new("10 mm", "10MM"), new("12 mm", "12MM"),
                new("14 mm", "14MM"), new("16 mm", "16MM"),
            ],
            Features =
            [
                "Machined from solid bar, then passivated against standing salt water.",
                "Rebuilds from a service kit rather than going in the bin.",
                "Load-rated and stamped, so the number is on the part rather than the box.",
                "Shaped so it does not foul the sheet when the load comes on.",
            ],
            Attributes =
            [
                new("working-load-kg", ["450", "700", "1,100", "1,600"]),
                new("breaking-load-kg", ["1,350", "2,100", "3,300"]),
                new("pin-diameter-mm", ["8", "10", "12", "16"]),
                new("corrosion", ["Passivated, 500 h salt spray", "Hard anodised"]),
                new("fastening", ["M6 countersunk", "M8 countersunk"]),
            ],
        },
        new()
        {
            Category = "navigation",
            Code = "NAV",
            VariantAxis = "Finish",
            ImageSubject = "compasses, dividers and barometers on wood or chart paper",
            MinPriceDollars = 89,
            MaxPriceDollars = 890,
            VariantStepDollars = 12,
            Types =
            [
                "Hand Bearing Compass", "Bulkhead Compass", "Parallel Rule", "Divider Set",
                "Aneroid Barometer", "Chart Magnifier", "Deck Watch", "Sextant",
            ],
            Materials =
            [
                "Solid Brass", "Cast Bronze", "Machined Aluminium",
                "Teak and Brass", "Chromed Steel", "Lacquered Brass",
            ],
            Options = [new("Polished", "POL"), new("Antiqued", "ANT"), new("Blackened", "BLK")],
            Features =
            [
                "Stays readable in a seaway instead of chasing every roll.",
                "Graduated by hand and checked against a certified reference.",
                "Reads under red light without spoiling your night vision.",
                "Ships in a fitted case that survives a locker.",
            ],
            Attributes =
            [
                new("graduation", ["1 degree", "2 degrees", "5 degrees"]),
                new("damping", ["Fluid, sapphire jewel", "Fluid, ceramic pivot"]),
                new("illumination", ["Luminous, tritium-free", "Red LED, 12 V"]),
                new("mount", ["Bulkhead", "Handheld", "Binnacle"]),
                new("accuracy", ["Plus or minus 1 degree", "Plus or minus 0.5 degrees"]),
            ],
        },
        new()
        {
            Category = "rope-and-rigging",
            Code = "RIG",
            VariantAxis = "Length",
            ImageSubject = "coiled line, splices and whippings",
            MinPriceDollars = 29,
            MaxPriceDollars = 319,
            VariantStepDollars = 26,
            Types =
            [
                "Double Braid Sheet", "Racing Halyard", "Dock Line", "Anchor Rode",
                "Lashing Line", "Whipping Twine", "Control Line", "Snubber",
            ],
            Materials =
            [
                "Polyester", "Dyneema SK78", "Nylon Three-Strand",
                "Vectran-Core", "Aramid-Blend", "Hemp-Look Polyester",
            ],
            Options =
            [
                new("10 m", "10M"), new("15 m", "15M"), new("20 m", "20M"),
                new("30 m", "30M"), new("50 m", "50M"),
            ],
            Features =
            [
                "Cover and core are spliceable, and the splice guide ships in the box.",
                "Pre-stretched, so what you tension in April is still there in August.",
                "Whipped and heat-sealed at both ends before it leaves the loft.",
                "Grippy for bare hands on a cold morning and still easy on the clutch.",
            ],
            Attributes =
            [
                new("diameter-mm", ["8", "10", "12", "14"]),
                new("breaking-load-kg", ["1,800", "2,600", "4,000", "6,200"]),
                new("construction", ["16-plait cover, braided core", "Three-strand laid", "12-plait single braid"]),
                new("elongation", ["Under 1 percent at 10 percent load", "3 percent at 10 percent load"]),
                new("splice", ["Eye splice available", "Whipped both ends"]),
            ],
        },
        new()
        {
            Category = "charts-and-books",
            Code = "CHT",
            VariantAxis = "Format",
            ImageSubject = "folded charts, almanacs and log books on a chart table",
            MinPriceDollars = 14,
            MaxPriceDollars = 79,
            VariantStepDollars = 6,
            Types =
            [
                "Coastal Chart", "Passage Almanac", "Pilot Guide", "Tide Table",
                "Chart Portfolio", "Log Book", "Cruising Companion",
            ],
            Materials =
            [
                "Waterproof Paper", "Linen-Bound", "Cloth-Bound",
                "Folded Card", "Laminated", "Tear-Resistant",
            ],
            Options = [new("Folded", "FLD"), new("Flat", "FLT"), new("Bound", "BND")],
            Features =
            [
                "Printed on stock that survives a wet chart table and a pencil eraser.",
                "Corrected to the most recent notices before each print run.",
                "Ruled for dead reckoning, with a margin wide enough to write in.",
                "Folds without putting a crease through the harbour you need.",
            ],
            Attributes =
            [
                new("scale", ["1:75,000", "1:40,000", "1:150,000"]),
                new("edition", ["2026", "2025"]),
                new("pages", ["48", "96", "192", "288"]),
                new("waterproof", ["Yes", "Water-resistant"]),
                new("binding", ["Wire-o", "Section-sewn", "Folded"]),
            ],
        },
        new()
        {
            Category = "knives-and-tools",
            Code = "TLS",
            VariantAxis = "Blade",
            ImageSubject = "rigging knives, fids and marlinspikes",
            MinPriceDollars = 34,
            MaxPriceDollars = 219,
            VariantStepDollars = 9,
            Types =
            [
                "Rigging Knife", "Marlinspike", "Splicing Fid Set", "Shackle Key",
                "Sail Repair Kit", "Bosun Multi-Tool", "Deck Knife",
            ],
            Materials =
            [
                "Sandvik Steel", "Carbon Steel", "Stainless",
                "Bronze", "Micarta-Handled", "Ash-Handled",
            ],
            Options = [new("60 mm", "60MM"), new("75 mm", "75MM"), new("90 mm", "90MM")],
            Features =
            [
                "Ground and finished by hand, then checked before it ships.",
                "Sized for one-handed use, which is the only hand you will have.",
                "Lanyard hole sized for 4 mm cord and a stopper knot.",
                "Stows in a roll that will not rattle across a locker.",
            ],
            Attributes =
            [
                new("steel", ["Sandvik 12C27", "440C stainless", "1095 carbon"]),
                new("length-mm", ["60", "75", "90"]),
                new("locking", ["Linerlock", "Slipjoint", "Framelock"]),
                new("handle", ["Micarta", "Stabilised ash", "Textured G10"]),
                new("lanyard-hole", ["Yes, 4 mm"]),
            ],
        },
        new()
        {
            Category = "lamps-and-lighting",
            Code = "LMP",
            VariantAxis = "Light",
            ImageSubject = "brass and bronze lamps lit against a dark cabin interior",
            MinPriceDollars = 59,
            MaxPriceDollars = 389,
            VariantStepDollars = 8,
            Types =
            [
                "Anchor Lamp", "Bulkhead Lantern", "Chart Light", "Cabin Sconce",
                "Storm Lantern", "Deck Flood", "Companionway Light",
            ],
            Materials =
            [
                "Solid Brass", "Copper", "Galvanised Steel",
                "Cast Bronze", "Powder-Coated Steel", "Chromed Brass",
            ],
            Options = [new("Warm White", "WW"), new("Neutral White", "NW"), new("Red Night", "RED")],
            Features =
            [
                "Runs on 12 V and draws less than a masthead tricolour.",
                "Switches to red for night watches without waking the off-watch.",
                "Gimballed, so it stays level while the boat does not.",
                "Sealed to IP66 and still serviceable with a screwdriver.",
            ],
            Attributes =
            [
                new("output-lumens", ["120", "240", "480", "900"]),
                new("power", ["12 V DC", "12/24 V DC"]),
                new("draw-amps", ["0.2", "0.4", "0.8"]),
                new("ip-rating", ["IP66", "IP65"]),
                new("dimming", ["Touch dimmer", "Rotary dimmer", "None"]),
            ],
        },
        new()
        {
            Category = "bags-and-storage",
            Code = "BAG",
            VariantAxis = "Capacity",
            ImageSubject = "duffels, dry bags and kit rolls, packed and empty",
            MinPriceDollars = 39,
            MaxPriceDollars = 279,
            VariantStepDollars = 22,
            Types =
            [
                "Sail Duffel", "Dry Bag", "Ditch Bag", "Chart Case",
                "Kit Roll", "Deck Tote", "Wet Locker Sack",
            ],
            Materials =
            [
                "Waxed Canvas", "Coated Ripstop", "Recycled Sailcloth",
                "Vulcanised Rubber", "Heavy Duck Canvas", "Ballistic Nylon",
            ],
            Options = [new("20 L", "20L"), new("35 L", "35L"), new("55 L", "55L"), new("90 L", "90L")],
            Features =
            [
                "Roll-top closure is welded rather than stitched, because stitches leak.",
                "Base is doubled where it meets a wet cockpit sole.",
                "Straps convert from carry to backpack with no buckle to lose.",
                "Drains through a grommet instead of holding a puddle.",
            ],
            Attributes =
            [
                new("capacity-litres", ["20", "35", "55", "90"]),
                new("closure", ["Roll-top, welded", "Two-way zip with a storm flap"]),
                new("base", ["Reinforced, welded", "Doubled canvas"]),
                new("grab-handles", ["Four", "Two"]),
                new("waterproof", ["Fully welded, IPX6", "Water-resistant"]),
            ],
        },
    ];
}
