using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VelaCommerce.Storefront.Catalog;

/// <summary>
/// The whole shop, held in memory.
/// <para>
/// This is the load-bearing decision of the storefront. The API and the database scale to
/// zero, so nothing on the first-paint path may touch them. Instead the client fetches one
/// static file from its own origin exactly once, builds a search index over it, and answers
/// every subsequent browse, search, filter, sort and page from RAM. After the first fetch
/// there is no network call and therefore no spinner — which is why searching feels instant
/// and why the storefront still works with the backend switched off entirely.
/// </para>
/// <para>
/// It deliberately holds no <c>HttpClient</c> pointed at the API and has no method that
/// could grow one. If a future feature needs live data (stock, pricing), it belongs in a
/// separate service that a component may await <em>after</em> paint, never here.
/// </para>
/// </summary>
public sealed class CatalogService
{
    /// <summary>
    /// The snapshot's path, relative to the app base address. A file, not an endpoint: it is
    /// served by whatever hosts the static assets, so it cannot be asleep when the shop opens.
    /// </summary>
    public const string SnapshotPath = "catalog.snapshot.json";

    private readonly HttpClient _http;

    /// <summary>Guards against two components each kicking off a fetch on first render.</summary>
    private Task? _load;

    private CatalogSnapshot? _snapshot;
    private IndexedProduct[] _index = [];
    private Dictionary<string, CatalogProduct> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private CatalogCategory[] _categories = [];

    /// <summary>Creates the service over the app's own base-address client.</summary>
    /// <param name="http">A client whose base address is the app origin, never the API.</param>
    public CatalogService(HttpClient http) => _http = http;

    /// <summary>Where the one-and-only load has got to.</summary>
    public CatalogLoadState State { get; private set; } = CatalogLoadState.Idle;

    /// <summary>
    /// A message written for a shopper, not a stack trace, populated when
    /// <see cref="State"/> is <see cref="CatalogLoadState.Failed"/>. The failure path is
    /// explicit because a silent blank grid is indistinguishable from an empty shop.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The technical detail behind <see cref="ErrorMessage"/>, shown in a disclosure rather than a dialog.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>True once queries will return real results.</summary>
    public bool IsReady => State == CatalogLoadState.Ready;

    /// <summary>The loaded snapshot itself, for provenance in the footer. Null until loaded.</summary>
    public CatalogSnapshot? Snapshot => _snapshot;

    /// <summary>The single currency the whole catalog is priced in.</summary>
    public string Currency => _snapshot?.Currency ?? "USD";

    /// <summary>Every category, in alphabetical order, with counts. Empty until loaded.</summary>
    public IReadOnlyList<CatalogCategory> Categories => _categories;

    /// <summary>Total products in the snapshot.</summary>
    public int ProductCount => _index.Length;

    /// <summary>
    /// Fetches and indexes the snapshot, once. Safe to await from every component's
    /// <c>OnInitializedAsync</c>: concurrent callers share the same in-flight task, and a
    /// completed load returns synchronously.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (State == CatalogLoadState.Ready)
            return Task.CompletedTask;

        return _load ??= LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Clears a failed load so the next <see cref="EnsureLoadedAsync"/> tries again. This is
    /// what the error panel's retry button calls; a shopper on a flaky connection should not
    /// have to reload the whole application.
    /// </summary>
    public void Reset()
    {
        if (State == CatalogLoadState.Ready)
            return;

        _load = null;
        State = CatalogLoadState.Idle;
        ErrorMessage = null;
        ErrorDetail = null;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        State = CatalogLoadState.Loading;
        ErrorMessage = null;
        ErrorDetail = null;

        try
        {
            var snapshot = await _http.GetFromJsonAsync(
                SnapshotPath,
                CatalogJsonContext.Default.CatalogSnapshot,
                cancellationToken).ConfigureAwait(false);

            if (snapshot is null || snapshot.Products.Count == 0)
            {
                Fail(
                    "The catalog snapshot loaded but contained no products.",
                    $"{SnapshotPath} parsed to an empty catalog. The file is probably a placeholder or was truncated in deployment.");
                return;
            }

            Build(snapshot);
            State = CatalogLoadState.Ready;
        }
        catch (HttpRequestException ex)
        {
            Fail(
                "The catalog could not be downloaded. This storefront reads its catalog from a static file, so there is nothing to browse until that file is reachable.",
                $"GET {SnapshotPath} failed{(ex.StatusCode is null ? string.Empty : $" with HTTP {(int)ex.StatusCode} {ex.StatusCode}")}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Fail(
                "The catalog downloaded but could not be read. It is probably from a newer or older build than this storefront.",
                $"{SnapshotPath} failed to parse: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(
                "The catalog could not be loaded.",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Fail(string message, string detail)
    {
        State = CatalogLoadState.Failed;
        ErrorMessage = message;
        ErrorDetail = detail;
    }

    /// <summary>
    /// Builds the derived structures once, at load, so no query ever allocates a search
    /// string or lowercases a name. 288 products is small, but the point is that a keystroke
    /// costs a scan over pre-built strings and nothing else.
    /// </summary>
    private void Build(CatalogSnapshot snapshot)
    {
        // The snapshot names its currency once rather than repeating it on 979 prices. Stamp
        // it onto every product and variant here so components downstream can ask for a
        // CatalogMoney without also having to be handed the snapshot.
        var currency = snapshot.Currency;
        var products = new CatalogProduct[snapshot.Products.Count];
        for (var i = 0; i < snapshot.Products.Count; i++)
        {
            var raw = snapshot.Products[i];
            products[i] = raw with
            {
                Currency = currency,
                Variants = [.. raw.Variants.Select(v => v with { Currency = currency })],
            };
        }

        _snapshot = snapshot with { Products = products };

        _index = new IndexedProduct[products.Length];
        for (var i = 0; i < products.Length; i++)
        {
            var product = products[i];

            // The generator precomputes the search text, so a keystroke is one substring test
            // per product rather than re-lowercasing four fields 288 times. Fall back to
            // building it here if an older snapshot has no blob.
            var searchText = string.IsNullOrEmpty(product.SearchBlob)
                ? BuildSearchText(product)
                : product.SearchBlob;

            _index[i] = new IndexedProduct(
                product,
                searchText,
                product.Name.ToLowerInvariant(),
                product.FromPriceMinorUnits,
                i);
        }

        _bySlug = products.ToDictionary(
            static p => p.Slug,
            static p => p,
            StringComparer.OrdinalIgnoreCase);

        _categories = [.. snapshot.Products
            .GroupBy(static p => p.Category, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new CatalogCategory(
                group.Key,
                SpecFormatter.TitleCaseFromSlug(group.Key),
                group.Count(),
                group
                    .Select(static p => p.FromPrice)
                    .Where(static m => m is not null)
                    .OrderBy(static m => m!.AmountMinorUnits)
                    .FirstOrDefault()))
            .OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Everything a shopper might plausibly type, flattened and lowercased once. SKUs are in
    /// here on purpose: someone holding a worn part reads the number off it.
    /// </summary>
    private static string BuildSearchText(CatalogProduct product)
    {
        var text = new StringBuilder(512);
        text.Append(product.Name).Append(' ')
            .Append(product.Category).Append(' ')
            .Append(SpecFormatter.TitleCaseFromSlug(product.Category)).Append(' ')
            .Append(product.VariantAxis).Append(' ')
            .Append(product.Description).Append(' ')
            .Append(product.Slug).Append(' ');

        foreach (var (key, value) in product.Attributes)
            text.Append(SpecFormatter.Label(key)).Append(' ').Append(value).Append(' ');

        foreach (var variant in product.Variants)
            text.Append(variant.Sku).Append(' ').Append(variant.Name).Append(' ');

        return text.ToString().ToLowerInvariant();
    }

    /// <summary>Looks a product up by its slug, for the detail route. Null when there is no such product.</summary>
    public CatalogProduct? FindBySlug(string? slug) =>
        slug is not null && _bySlug.TryGetValue(slug, out var product) ? product : null;

    /// <summary>Looks a category up by slug, so a route can tell "no such category" from "empty category".</summary>
    public CatalogCategory? FindCategory(string? slug) =>
        slug is null
            ? null
            : _categories.FirstOrDefault(c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Answers one grid query entirely from memory. Filter, then sort, then take a page —
    /// in that order, so the page numbers describe the filtered set and not the catalog.
    /// </summary>
    public CatalogPage Query(CatalogQuery query)
    {
        if (!IsReady)
            return CatalogPage.Empty;

        var matches = Filter(query).ToList();
        var sorted = Sort(matches, query.Sort);

        var pageSize = Math.Max(1, query.PageSize);
        var pageCount = Math.Max(1, (int)Math.Ceiling(sorted.Count / (double)pageSize));
        var page = Math.Clamp(query.Page, 1, pageCount);

        var items = sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(static indexed => indexed.Product)
            .ToArray();

        return new CatalogPage(items, sorted.Count, page, pageCount, pageSize);
    }

    /// <summary>
    /// The attribute filters worth offering for a scope, built from the products actually in
    /// it. Keys carried by fewer than two products, or with only one distinct value, are
    /// dropped: a facet that cannot narrow anything is furniture.
    /// </summary>
    public IReadOnlyList<AttributeFacet> Facets(CatalogQuery scope, int maxValuesPerFacet = 12)
    {
        if (!IsReady)
            return [];

        // Facet counts describe the search and category scope but ignore the attribute
        // selections themselves, so choosing one value does not erase its own siblings.
        var scopeWithoutAttributes = scope with { Attributes = [] };
        var products = Filter(scopeWithoutAttributes).Select(static i => i.Product).ToList();

        var byKey = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
        {
            foreach (var (key, value) in product.Attributes)
            {
                if (!byKey.TryGetValue(key, out var values))
                {
                    values = new Dictionary<string, int>(StringComparer.Ordinal);
                    byKey[key] = values;
                }

                values[value] = values.GetValueOrDefault(value) + 1;
            }
        }

        return
        [
            .. byKey
                .Where(static entry => entry.Value.Count > 1)
                .Select(entry => new AttributeFacet(
                    entry.Key,
                    SpecFormatter.Label(entry.Key),
                    SpecFormatter.Unit(entry.Key),
                    [
                        .. entry.Value
                            .OrderByDescending(static v => v.Value)
                            .ThenBy(static v => v.Key, StringComparer.OrdinalIgnoreCase)
                            .Take(maxValuesPerFacet)
                            .Select(static v => new AttributeFacetValue(v.Key, v.Value))
                    ]))
                .OrderBy(static facet => facet.Label, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private IEnumerable<IndexedProduct> Filter(CatalogQuery query)
    {
        IEnumerable<IndexedProduct> candidates = _index;

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category;
            candidates = candidates.Where(i =>
                string.Equals(i.Product.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Attributes.Count > 0)
        {
            var filters = query.Attributes;
            candidates = candidates.Where(i =>
                filters.All(f =>
                    i.Product.Attributes.TryGetValue(f.Key, out var value) &&
                    string.Equals(value, f.Value, StringComparison.OrdinalIgnoreCase)));
        }

        // Every whitespace-separated term must appear somewhere in the product's indexed
        // text. AND rather than OR: "brass lamp" should narrow, not widen.
        var terms = Tokenise(query.Search);
        if (terms.Length > 0)
            candidates = candidates.Where(i => MatchesAll(i.SearchText, terms));

        return candidates;
    }

    private static string[] Tokenise(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? []
            : search.ToLowerInvariant().Split(
                (char[])[' ', '\t', '\n', '\r', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesAll(string haystack, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!haystack.Contains(term, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Sorting is always tie-broken by the snapshot's own index so that two products at the
    /// same price never swap places between renders.
    /// </summary>
    private static List<IndexedProduct> Sort(List<IndexedProduct> matches, CatalogSort sort) => sort switch
    {
        // Unpriced products sort last in both price orders rather than pretending to be free.
        CatalogSort.PriceLowToHigh =>
            [.. matches.OrderBy(static i => i.SortPrice ?? long.MaxValue).ThenBy(static i => i.Ordinal)],
        CatalogSort.PriceHighToLow =>
            [.. matches.OrderByDescending(static i => i.SortPrice ?? long.MinValue).ThenBy(static i => i.Ordinal)],
        CatalogSort.NameAToZ =>
            [.. matches.OrderBy(static i => i.SortName, StringComparer.Ordinal).ThenBy(static i => i.Ordinal)],
        CatalogSort.NameZToA =>
            [.. matches.OrderByDescending(static i => i.SortName, StringComparer.Ordinal).ThenBy(static i => i.Ordinal)],
        _ => [.. matches.OrderBy(static i => i.Ordinal)],
    };

    /// <summary>
    /// A product with its query-time keys precomputed. Private because the shape exists to
    /// make keystrokes cheap, not to be part of anyone's contract.
    /// </summary>
    private readonly record struct IndexedProduct(
        CatalogProduct Product,
        string SearchText,
        string SortName,
        long? SortPrice,
        int Ordinal);
}
