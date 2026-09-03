using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The test that exists because the cart endpoints once shipped unmapped.
/// <para>
/// Every other test in this suite drives the composed host, and would happily go green against a
/// host that had been helped along: <see cref="CheckoutHost"/> supplies whatever piece of the
/// checkout composition it finds missing, so that a slice which is not yet wired can still be
/// tested rather than reporting fifty identical routing 404s. That helpfulness is a hazard on its
/// own — it is exactly how an endpoint reaches production without ever having been mapped there —
/// so the host records everything it had to supply and this test refuses to pass while the list is
/// not empty.
/// </para>
/// <para>
/// It is the only test here that asserts on the application's composition rather than on its
/// behaviour, and it is deliberately the one with the longest failure message: what it has to
/// communicate is not "something is broken" but "here are the lines Program.cs is missing".
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CheckoutWiringTests(PostgresFixture fixture)
{
    /// <summary>
    /// The deployed host maps the checkout surface and registers what it needs, with no help from
    /// the test project.
    /// </summary>
    [Fact]
    public async Task Program_composes_the_checkout_surface_itself()
    {
        using var host = new CheckoutHost(fixture.ConnectionString);
        using var client = host.NewBrowser();

        // A request, so the pipeline is built and every gap has been discovered before the list is
        // read. Any answer will do; what is under test is who answered.
        using var response = await client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(
            host.ComposedForYou.Count == 0,
            "The checkout surface is not wired into Program.cs, so the test host composed it "
            + "instead. These tests then prove that checkout works — but not that the deployed "
            + "application serves it, which is a different claim and the one that actually ships. "
            + "Add to src/VelaCommerce.Api/Program.cs, in this order:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, host.ComposedForYou.Select(line => "    " + line))
            + Environment.NewLine
            + "The services go with the other builder.Services lines; app.MapCheckoutEndpoints() "
            + "goes after app.UseDemoSession() and beside app.MapCartEndpoints(), because an order "
            + "needs a session to own it. Rebuild the Api project afterwards so openapi.json "
            + "regenerates, or CI will fail on the drift.");
    }

    /// <summary>
    /// The test host can read the application's routing table, which is the only reason its
    /// fallback is safe.
    /// <para>
    /// The fallback maps the checkout endpoints only when it cannot already find them, and two
    /// identical route patterns in one matcher are an ambiguous match — an exception on every
    /// checkout rather than a quiet duplicate. So the fallback's ability to see what the host
    /// already serves is load-bearing, and it depends on a framework detail rather than on a
    /// documented API. This asserts it against routes that are unquestionably mapped: if a future
    /// version of ASP.NET Core stops exposing them here, this test says so plainly instead of the
    /// whole suite failing with an ambiguity nobody expected.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_test_host_can_see_the_routes_the_application_maps()
    {
        using var host = new CheckoutHost(fixture.ConnectionString);
        using var client = host.NewBrowser();

        using var response = await client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(host.ObservedRoutes, route => route.Contains("api/cart", StringComparison.Ordinal));
        Assert.Contains(host.ObservedRoutes, route => route.Contains("api/catalog", StringComparison.Ordinal));
    }

    /// <summary>
    /// A payment signing secret this host's environment will accept is in configuration before the
    /// application registers anything.
    /// <para>
    /// This guards the one way wiring checkout into <c>Program.cs</c> could break the whole suite
    /// at once rather than one test at a time. <c>AddPaymentSimulator</c> validates while services
    /// are being registered, and outside Development it refuses the committed development secret —
    /// correctly, since that secret is in the repository. This host runs as Production, exactly as
    /// the shared demo does, so the application composing the simulator here would throw at startup
    /// and every checkout test would report a host that never came up.
    /// </para>
    /// <para>
    /// The secret is therefore published as an environment variable rather than through anything
    /// this factory configures, because the default configuration builder reads environment
    /// variables before <c>Program</c> runs and nothing else is guaranteed to be that early. If a
    /// future change breaks that path this test fails on its own, with a sentence, instead of the
    /// suite failing with a startup exception.
    /// </para>
    /// </summary>
    [Fact]
    public void A_signing_secret_the_production_environment_accepts_is_in_place_before_startup()
    {
        using var host = new CheckoutHost(fixture.ConnectionString);
        using var client = host.NewBrowser();

        var configuration = host.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(CheckoutHost.TestSigningSecret, configuration["Payments:Simulator:SigningSecret"]);
    }
}
