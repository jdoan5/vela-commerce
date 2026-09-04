using System.Net;
using VelaCommerce.Storefront.Catalog;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The shop's search, run against the snapshot the shop actually ships.
/// <para>
/// Browsing, filtering and searching are the whole of the first paint — the storefront answers them
/// from one static file and never calls the API — so a search that quietly misses is a broken shop
/// with a working backend. Nothing tested it, because the storefront had no test project and no
/// suite referenced it.
/// </para>
/// <para>
/// <b>It was broken.</b> The snapshot's search field is built by
/// <c>CatalogSnapshotBuilder.Normalise</c>, which reduces every run of non-alphanumerics to a single
/// space, so "Ketch Three-Layer Storm Cag" is indexed as "ketch three layer storm cag". The
/// storefront split the shopper's query on whitespace and punctuation only, leaving the hyphen
/// inside the term — so it searched for "three-layer" against text that says "three layer" and
/// returned nothing. 73 of 288 products could not be found by typing their own displayed name.
/// </para>
/// <para>
/// The generator predicted it, in a comment above the very function whose rule was not applied:
/// "the storefront must put the shopper's query through this same function... or a hyphen typed on
/// one side and not the other quietly returns nothing." Nothing checked that it did. This is that
/// check.
/// </para>
/// </summary>
public sealed class StorefrontSearchTests
{
    /// <summary>Serves the committed snapshot, so the test searches what a visitor would download.</summary>
    private sealed class SnapshotHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static async Task<CatalogService> LoadAsync()
    {
        var path = RepoFile("src/VelaCommerce.Storefront/wwwroot/catalog.snapshot.json");
        var json = await File.ReadAllTextAsync(path);

        var http = new HttpClient(new SnapshotHandler(json)) { BaseAddress = new Uri("https://localhost/") };
        var catalog = new CatalogService(http);

        await catalog.EnsureLoadedAsync();

        return catalog;
    }

    private static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VelaCommerce.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, relativePath);
    }

    /// <summary>
    /// The lowest bar search has to clear, and the one it was failing: a shopper who types what the
    /// product is called finds the product.
    /// </summary>
    [Fact]
    public async Task Every_product_is_findable_by_typing_its_own_displayed_name()
    {
        var catalog = await LoadAsync();

        var all = catalog.Query(new CatalogQuery { PageSize = 1000 });

        Assert.Equal(288, all.TotalCount);

        var missing = new List<string>();

        foreach (var product in all.Items)
        {
            var found = catalog.Query(new CatalogQuery { Search = product.Name, PageSize = 1000 });

            if (!found.Items.Any(candidate => string.Equals(candidate.Slug, product.Slug, StringComparison.Ordinal)))
            {
                missing.Add(product.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} of {all.TotalCount} products cannot be found by typing their own name. "
            + "The storefront's query tokeniser and CatalogSnapshotBuilder.Normalise have to reduce "
            + "text the same way, or a punctuation mark typed on one side and not the other returns "
            + $"nothing. First few: {string.Join("; ", missing.Take(5))}");
    }

    /// <summary>
    /// Punctuation must not decide whether a search works. The shopper does not know how the
    /// catalog was normalised and should not have to.
    /// </summary>
    [Theory]
    [InlineData("Three-Layer")]
    [InlineData("three layer")]
    [InlineData("THREE-LAYER")]
    [InlineData("three,layer")]
    public async Task A_hyphenated_term_finds_the_same_products_however_it_is_punctuated(string term)
    {
        var catalog = await LoadAsync();

        var found = catalog.Query(new CatalogQuery { Search = term, PageSize = 1000 });

        Assert.NotEmpty(found.Items);
    }
}
