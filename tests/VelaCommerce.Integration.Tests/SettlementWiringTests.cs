using System.Net;
using System.Text;

using VelaCommerce.Api.Endpoints;
using VelaCommerce.Infrastructure.Messaging;

using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The test that exists because this phase was built by four hands and nobody owns
/// <c>Program.cs</c>.
///
/// <para>
/// Every other test in this file's neighbourhood drives the composed host and would go green
/// against an application that had been helped along: <see cref="SettlementHost"/> supplies
/// whatever piece of the settlement composition it finds missing, so that a slice which is not yet
/// wired can still be proven rather than reporting a wall of routing 404s. That helpfulness is a
/// hazard of its own — it is precisely how the cart endpoints once reached production without ever
/// having been mapped there — so the host records everything it had to supply and this test
/// refuses to pass while the list is not empty.
/// </para>
///
/// <para>
/// It is the only test here that asserts on the application's composition rather than on its
/// behaviour, and deliberately the one with the longest failure message: what it has to
/// communicate is not "something is broken" but "here are the lines <c>Program.cs</c> is missing",
/// in the order they should be added.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SettlementWiringTests(PostgresFixture fixture)
{
    /// <summary>
    /// The deployed host registers the outbox and the timeline and maps the settlement receiver,
    /// with no help from the test project.
    /// </summary>
    [Fact]
    public async Task Program_composes_the_settlement_surface_itself()
    {
        using var host = new SettlementHost(fixture.ConnectionString);
        using var client = host.NewBrowser();

        // A request, so the pipeline is built and every gap has been discovered before the list is
        // read. Any answer will do; what is under test is who answered.
        using var response = await client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(
            host.ComposedForYou.Count == 0,
            "The settlement surface is not wired into Program.cs, so the test host composed it "
            + "instead. These tests then prove that duplicate, out-of-order and delayed delivery "
            + "are handled — but not that the deployed application handles them, which is a "
            + "different claim and the one that actually ships. Add to "
            + "src/VelaCommerce.Api/Program.cs:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, host.ComposedForYou.Select(line => "    " + line))
            + Environment.NewLine
            + "The services go with the other builder.Services lines and both take root "
            + "configuration, not a section. app.MapWebhookEndpoints() goes beside "
            + "app.MapCheckoutEndpoints(); it needs no session, because a payment gateway has no "
            + "cookie. Rebuild the Api project afterwards so openapi.json regenerates with the "
            + "/api/payments/webhook path, or the openapi-is-current job will fail on the drift.");
    }

    /// <summary>
    /// The sender's default path and the receiver's route are the same string.
    /// <para>
    /// This is the phase's one loose coupling: the dispatcher posts to
    /// <see cref="OutboxOptions.DefaultReceiverPath"/> and the receiver is mapped at
    /// <see cref="WebhookEndpoints.SettlementRoute"/>, and nothing in the type system joins them —
    /// deliberately, because the sender's value is a default a deployment may override and a route
    /// that moved whenever somebody retuned it would be a stranger failure than the mismatch. The
    /// cost of that choice is that the two can drift silently, and the symptom is settlements
    /// going undelivered rather than anything failing to compile. So it is asserted here, where
    /// the failure is one line long.
    /// </para>
    /// </summary>
    [Fact]
    public void The_dispatcher_posts_to_the_path_the_receiver_is_mapped_at() =>
        Assert.Equal(OutboxOptions.DefaultReceiverPath, WebhookEndpoints.SettlementRoute);

    /// <summary>
    /// The test host can read the application's routing table, which is the only reason its
    /// fallback is safe.
    /// <para>
    /// The fallback maps the settlement receiver only when it cannot already find it, and two
    /// identical route patterns in one matcher are an ambiguous match — an exception on every
    /// delivery rather than a quiet duplicate. So the fallback's ability to see what the host
    /// already serves is load-bearing, and it rests on a framework detail rather than a documented
    /// API. This asserts it against routes that are unquestionably mapped, so that a future
    /// version of ASP.NET Core hiding them says so plainly instead of failing the whole suite with
    /// an ambiguity nobody expected.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_test_host_can_see_the_routes_the_application_maps()
    {
        using var host = new SettlementHost(fixture.ConnectionString);
        using var client = host.NewBrowser();

        using var response = await client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(host.ObservedRoutes, route => route.Contains("api/cart", StringComparison.Ordinal));
        Assert.Contains(host.ObservedRoutes, route => route.Contains("api/checkout", StringComparison.Ordinal));
    }

    /// <summary>
    /// The settlement route is served and its handler can be activated from the container.
    /// <para>
    /// The claim worth making is that <c>MapWebhookEndpoints</c> is the <em>entire</em> wiring:
    /// there is no companion <c>AddWebhooks</c>, because every service the handler takes is one
    /// the host already registers, which leaves no second call for a composition root to forget.
    /// A missing registration would surface here as a 500 from the activator; a missing route as a
    /// 404. An unsigned body is used on purpose — a refusal that costs one parse and never reaches
    /// PostgreSQL is enough to prove the endpoint is alive.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_settlement_receiver_is_served_and_can_be_activated()
    {
        using var host = new SettlementHost(fixture.ConnectionString);
        using var client = host.NewGateway();

        using var response = await client.PostAsync(
            WebhookEndpoints.SettlementRoute,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
