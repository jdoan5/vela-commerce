using System.Net.Http.Json;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The server-side product search, and the escaping nothing was checking.
/// <para>
/// <c>GET /api/catalog/products?q=</c> has existed since the catalog slice and had no test of any
/// kind. <c>StorefrontSearchTests</c> covers a different mechanism entirely — the client-side index
/// the WebAssembly shop builds over the static snapshot — and none of the Bruno catalog requests
/// passed <c>q</c>. The README went further and said the API had no server-side search at all,
/// which is how a whole endpoint ends up unasserted: nobody was looking for it.
/// </para>
/// <para>
/// The interesting half is <c>EscapeForLike</c>. A shopper searching for "50%" must get products,
/// not the catalog: in <c>LIKE</c>/<c>ILIKE</c> a bare <c>%</c> matches any run of characters and a
/// bare <c>_</c> matches any single one, so an unescaped term turns a search box into a query
/// language. Not an injection — the term is a parameter and always was — but the difference between
/// a search that filters and one that does not.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogSearchTests : IDisposable
{
    private readonly Storefront _shop;

    public CatalogSearchTests(PostgresFixture fixture) => _shop = new Storefront(fixture);

    public void Dispose() => _shop.Dispose();

    /// <summary>
    /// A wildcard in the search box is a character to find, not an instruction to match everything.
    /// <para>
    /// The <c>%</c> has to sit in the MIDDLE of the term, and the pair has to differ only across
    /// it. The first version of this test searched for "50%" with the wildcard trailing, and it
    /// passed with the escaping deleted entirely — a trailing <c>%</c> lands next to the one the
    /// pattern already wraps the term in, so it changes nothing. Measured, not reasoned: the
    /// mutation reddened the other three tests and left this one green.
    /// </para>
    /// <para>
    /// With the wildcard in the middle the two seeded names are the whole argument. Escaped, the
    /// term matches only the literal "50%off". Unescaped it means "50, then anything, then off",
    /// which is also the second product — so the count is 1 or 2 depending on whether the escaping
    /// is there, and nothing else about the test has to change to say so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_percent_in_the_search_term_is_matched_literally()
    {
        var marker = Marker();

        await _shop.StockAsync($"Storm sail {marker} 50%off today", onHand: 2);
        await _shop.StockAsync($"Storm sail {marker} 50 percent off", onHand: 2);

        var hits = await SearchAsync($"{marker} 50%off");

        Assert.Single(hits);
        Assert.Contains("50%off", hits[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for <c>_</c>, which is the one people forget because it looks harmless.
    /// <para>
    /// Unescaped it matches any single character, so "a_b" finds "axb". Seeded here as a pair that
    /// differ only in that position, so the underscore has to be literal for the count to be one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_underscore_in_the_search_term_is_matched_literally()
    {
        var marker = Marker();

        await _shop.StockAsync($"Cleat {marker}a_b bronze", onHand: 2);
        await _shop.StockAsync($"Cleat {marker}axb bronze", onHand: 2);

        var hits = await SearchAsync($"{marker}a_b");

        Assert.Single(hits);
        Assert.Contains("a_b", hits[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A backslash is doubled before the wildcards are escaped, or it escapes the escapes.
    /// <para>
    /// This is the ordering bug <c>EscapeForLike</c>'s own comment warns about: escape <c>%</c>
    /// first and then double the backslashes, and the <c>\</c> the escaping just added gets doubled
    /// too, so the pattern means something else entirely. Only a term containing a literal
    /// backslash next to a wildcard can tell the two orderings apart.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_backslash_beside_a_wildcard_survives_the_escaping()
    {
        var marker = Marker();

        await _shop.StockAsync($"Chart {marker} scale 1\\%2 metric", onHand: 2);
        await _shop.StockAsync($"Chart {marker} scale 1 to 2 metric", onHand: 2);

        var hits = await SearchAsync($"{marker} scale 1\\%2");

        Assert.Single(hits);
        Assert.Contains("1\\%2", hits[0], StringComparison.Ordinal);
    }

    /// <summary>An ordinary word still finds what it should, so the escaping has not broken search.</summary>
    [Fact]
    public async Task An_ordinary_term_still_matches()
    {
        var marker = Marker();

        await _shop.StockAsync($"Binnacle {marker} brass", onHand: 2);
        await _shop.StockAsync($"Binnacle {marker} chrome", onHand: 2);

        var hits = await SearchAsync(marker);

        Assert.Equal(2, hits.Count);
    }

    /// <summary>
    /// A term unique to one test, taken from the END of a UUIDv7.
    /// <para>
    /// The leading half of a v7 is 48 bits of Unix milliseconds, so a <em>prefix</em> is mostly
    /// timestamp and two markers minted in the same second share it. Written that way first, these
    /// four tests found each other's products and the count assertion read 4 where it expected 2 —
    /// which is the failure being useful, since a test that silently matched a neighbour's rows
    /// would have proved nothing about escaping.
    /// </para>
    /// </summary>
    private static string Marker() => Guid.CreateVersion7().ToString("N")[^10..];

    /// <summary>Product names for one search term, straight off the wire.</summary>
    private async Task<IReadOnlyList<string>> SearchAsync(string term)
    {
        using var client = _shop.Host.NewBrowser();

        using var response = await client.GetAsync($"/api/catalog/products?q={Uri.EscapeDataString(term)}&pageSize=50");

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<SearchPage>();

        Assert.NotNull(page);

        return [.. page.Items.Select(item => item.Name)];
    }

    private sealed record SearchPage(IReadOnlyList<SearchItem> Items);

    private sealed record SearchItem(string Name);
}
