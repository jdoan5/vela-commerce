namespace VelaCommerce.Storefront.Catalog;

/// <summary>
/// The orders a shopper can put the grid in. <see cref="Featured"/> is the snapshot's own
/// order, which the generator already interleaves by category, so it is a real default
/// rather than "unsorted".
/// </summary>
public enum CatalogSort
{
    /// <summary>Snapshot order. Stable across reloads because the snapshot is deterministic.</summary>
    Featured,

    /// <summary>Cheapest from-price first.</summary>
    PriceLowToHigh,

    /// <summary>Dearest from-price first.</summary>
    PriceHighToLow,

    /// <summary>Alphabetical by product name.</summary>
    NameAToZ,

    /// <summary>Reverse alphabetical by product name.</summary>
    NameZToA,
}

/// <summary>
/// One attribute the shopper has narrowed to, as a key and an exact value taken from the
/// snapshot — "breaking-load-kg" is "3,300", not "over 3000". Facet values are compared as
/// written so the filter can never disagree with the value printed on the card.
/// </summary>
public readonly record struct AttributeFilter(string Key, string Value);

/// <summary>
/// Everything the grid asks the catalog for, in one value.
/// <para>
/// It is a record so a page can hold one field of state and derive the next query with a
/// <c>with</c> expression, which makes "changing the search resets to page one" a single
/// visible line rather than a rule spread over five event handlers.
/// </para>
/// </summary>
public sealed record CatalogQuery
{
    /// <summary>Free text. Matched against name, category, description and spec values; all terms must hit.</summary>
    public string? Search { get; init; }

    /// <summary>A category slug such as <c>rope-and-rigging</c>, or null for the whole catalog.</summary>
    public string? Category { get; init; }

    /// <summary>Attribute narrowings, combined with AND. Empty by default.</summary>
    public IReadOnlyList<AttributeFilter> Attributes { get; init; } = [];

    /// <summary>The requested order.</summary>
    public CatalogSort Sort { get; init; } = CatalogSort.Featured;

    /// <summary>One-based page number. Clamped by the service, never trusted.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Products per page.</summary>
    public int PageSize { get; init; } = 24;
}

/// <summary>
/// One page of results plus the counts the toolbar and pager need. The totals come back
/// with the items so the UI never has to run a second query just to say "288 products".
/// </summary>
public sealed record CatalogPage(
    IReadOnlyList<CatalogProduct> Items,
    int TotalCount,
    int Page,
    int PageCount,
    int PageSize)
{
    /// <summary>An empty result, used before the snapshot has loaded so the grid has something real to render.</summary>
    public static CatalogPage Empty { get; } = new([], 0, 1, 1, 24);

    /// <summary>One-based index of the first item on this page, for "showing 25–48 of 288".</summary>
    public int FirstItemNumber => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    /// <summary>One-based index of the last item on this page.</summary>
    public int LastItemNumber => TotalCount == 0 ? 0 : FirstItemNumber + Items.Count - 1;

    /// <summary>True when there is a page before this one.</summary>
    public bool HasPrevious => Page > 1;

    /// <summary>True when there is a page after this one.</summary>
    public bool HasNext => Page < PageCount;
}

/// <summary>
/// A category as the navigation needs it: the slug for routing, a display name derived
/// once, how many products are in it, and the cheapest entry point.
/// </summary>
public sealed record CatalogCategory(string Slug, string Name, int ProductCount, CatalogMoney? FromPrice);

/// <summary>One selectable value inside a facet, with the number of products it would leave.</summary>
public sealed record AttributeFacetValue(string Value, int ProductCount);

/// <summary>
/// One attribute key offered as a filter, already labelled for display. Built from the
/// products actually in scope, so a facet is never shown with nothing behind it.
/// </summary>
public sealed record AttributeFacet(string Key, string Label, string? Unit, IReadOnlyList<AttributeFacetValue> Values);

/// <summary>Where the snapshot load has got to. Drives whether the page shows skeletons, results or an error.</summary>
public enum CatalogLoadState
{
    /// <summary>Nothing requested yet.</summary>
    Idle,

    /// <summary>The fetch is in flight. The grid shows skeletons sized like real cards.</summary>
    Loading,

    /// <summary>The snapshot is parsed and in memory. Everything after this is instant.</summary>
    Ready,

    /// <summary>The fetch or the parse failed. The page must say so out loud, and offer a retry.</summary>
    Failed,
}
