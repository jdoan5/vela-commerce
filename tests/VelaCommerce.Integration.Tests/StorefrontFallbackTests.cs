using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The single-page fallback, and the paths it must never answer for.
/// <para>
/// The storefront is a deep-linked SPA, so a request for <c>/p/bronze-cleat</c> has to come back as
/// the shell rather than a 404 — the route exists only in the browser. That same rule, left
/// unqualified, will answer <em>every</em> unmatched GET with an HTML document, which is how a
/// fetch expecting JSON ends up failing to parse a web page and a server-rendered page at
/// <c>/admin</c> silently becomes the shop.
/// </para>
/// <para>
/// <b>This host is built rather than reused, because the shared ones cannot test it.</b> They run
/// as Production, and <c>StorefrontAssets.Locate</c> deliberately refuses to reach across the
/// repository into a sibling project's build output outside Development — so it finds nothing, the
/// fallback stands down for want of a shell, and every path 404s for the wrong reason. A test
/// asserting a 404 there would pass whatever the reserved list said. This one points
/// <c>Storefront:Root</c> at a directory holding a real <c>index.html</c>, then proves the fallback
/// is live before asserting where it stops.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StorefrontFallbackTests : IDisposable
{
    private const string Shell = "<!doctype html><html><body>storefront shell</body></html>";

    private readonly string _root;
    private readonly WebApplicationFactory<Program> _host;

    public StorefrontFallbackTests(PostgresFixture fixture)
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-shell-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.html"), Shell);

        _host = new ShellHost(fixture.ConnectionString, _root);
    }

    public void Dispose()
    {
        _host.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ShellHost(string connectionString, string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);

            // The explicit-path branch of StorefrontAssets.Locate, which is the one branch that
            // works outside Development and the one a deployment actually uses.
            builder.UseSetting("Storefront:Root", root);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<VelaCommerceDbContext>>();
                services.RemoveAll<DbContextOptions<VelaCommerceDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<VelaCommerceDbContext>();
                services.AddDbContext<VelaCommerceDbContext>(options => options.UseNpgsql(connectionString));

                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

                services.RemoveAll<ReservationReaperOptions>();
                services.AddSingleton(new ReservationReaperOptions { Enabled = false });
            });
        }
    }

    /// <summary>
    /// The precondition every assertion below depends on. If this fails, the others prove nothing.
    /// </summary>
    [Fact]
    public async Task A_deep_link_the_server_does_not_know_is_answered_with_the_shell()
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync("/p/some-product-only-the-client-routes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("storefront shell", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/admin</c> is reserved before any admin page exists, and this is what keeps it reserved.
    /// Removing it from <c>ReservedPrefixes</c> turns a mistyped admin URL into the shop, with no
    /// error and nothing in a log — and later, once the pages are real, turns a routing mistake
    /// into a page that silently renders the wrong application.
    /// </summary>
    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/orders")]
    [InlineData("/admin/a-page-that-does-not-exist")]
    public async Task The_shell_never_answers_for_an_admin_path(string path)
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("storefront shell", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason the reserved list exists at all: a caller asking for JSON must be told the route
    /// is missing, not handed a web page to fail parsing.
    /// </summary>
    [Theory]
    [InlineData("/api/there-is-no-such-endpoint")]
    [InlineData("/health/nope")]
    public async Task The_shell_never_answers_for_a_route_this_host_owns(string path)
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("storefront shell", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
