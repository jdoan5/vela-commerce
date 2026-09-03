using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VelaCommerce.Api.Endpoints;
using VelaCommerce.Domain.Payments;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Payments;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The real API host, in-process, pointed at the test container, with the checkout surface
/// reachable.
/// <para>
/// It is a near-twin of <see cref="DemoSessionHost"/> and deliberately does not subclass it — that
/// type is sealed, and the substitutions below have to happen in the same
/// <c>ConfigureTestServices</c> callback as the ones it makes. The three shared substitutions carry
/// the same reasoning it documents at length: the connection string is replaced rather than
/// configured so an ambient <c>VELA_DB_CONNECTION</c> cannot redirect the suite at the developer's
/// own database; the Data Protection key ring is ephemeral so real cryptography is exercised while
/// nothing is written to the developer's home directory and no cookie survives between hosts; and
/// the environment is Production because that is what the shared demo runs as and because
/// Development would migrate and parse the 400 KB catalog seed into the container on every host.
/// </para>
/// <para>
/// <strong>Why this file also composes things Program.cs is supposed to compose.</strong> The
/// checkout slice ships extension methods and, by a rule this repository learned the hard way, does
/// not wire them: the host owns its own composition. Until that wiring lands, a test suite driving
/// the composed host would be a wall of routing 404s that says nothing about whether checkout
/// works. So this host fills whatever gap it finds — and <em>records every gap it filled</em>, so
/// that <see cref="CheckoutWiringTests"/> can fail with the exact lines Program.cs is missing
/// rather than leaving the omission to be discovered in production, which is precisely how the cart
/// endpoints once shipped unmapped. Every fill is a no-op the moment the host does it properly:
/// the service registrations all go through <c>TryAdd</c>, and the endpoint fallback checks first
/// and stands down.
/// </para>
/// </summary>
public sealed class CheckoutHost : WebApplicationFactory<Program>
{
    /// <summary>
    /// A signing secret for the payment simulator, long enough to satisfy
    /// <c>PaymentSimulatorOptions.Validate</c> and different from the committed development
    /// default, which that method refuses outside Development.
    /// <para>
    /// It is not a secret in any meaningful sense — it signs simulated webhooks in a test process
    /// — and it is written down here rather than generated so that a failing signature is
    /// reproducible.
    /// </para>
    /// </summary>
    public const string TestSigningSecret = "vela-integration-tests-hmac-key-0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Published as an environment variable, and that is the only channel that is guaranteed to
    /// arrive in time.
    /// <para>
    /// <c>AddPaymentSimulator</c> reads configuration and validates it <em>while services are being
    /// registered</em> — before <c>ConfigureTestServices</c> runs, and before anything this factory
    /// can add to the configuration pipeline is guaranteed to be visible. Under this host's
    /// Production environment the committed development secret is refused, so a host that composed
    /// the simulator the way the guidance recommends would fail to start at all and every test here
    /// would report a startup exception instead of a checkout result. The default configuration
    /// builder reads environment variables before <c>Program</c> registers anything, so setting one
    /// is the one hook that is always early enough.
    /// </para>
    /// <para>
    /// Set from a static constructor so it is in place however the first host is built, and set
    /// process-wide because that is what an environment variable is; the value is inert for any
    /// code that does not read the <c>Payments:Simulator</c> section.
    /// </para>
    /// </summary>
    static CheckoutHost() =>
        Environment.SetEnvironmentVariable("Payments__Simulator__SigningSecret", TestSigningSecret);

    private readonly string _connectionString;
    private readonly List<string> _composedForYou = [];
    private readonly List<string> _observedRoutes = [];

    /// <summary>Binds a host to the container the fixture has already started and migrated.</summary>
    /// <param name="connectionString"><see cref="PostgresFixture.ConnectionString"/>.</param>
    public CheckoutHost(string connectionString)
    {
        _connectionString = connectionString;

        // https on a server that never negotiates TLS, for the reason DemoSessionHost gives: the
        // session cookie ships with Secure set, and CookieContainer would accept it over http and
        // then refuse to send it back, so every request would look like a new visitor and the
        // idempotency and ownership tests would pass for entirely the wrong reason.
        ClientOptions.BaseAddress = new Uri("https://localhost/");
    }

    /// <summary>
    /// The composition this host had to supply because the application did not, in the order it
    /// found the gaps. Empty is the goal; anything in it is a line missing from <c>Program.cs</c>.
    /// Populated while the host is built, so read it only after a client has been created.
    /// </summary>
    public IReadOnlyList<string> ComposedForYou => _composedForYou;

    /// <summary>
    /// Every route pattern the application had mapped by the time the pipeline was built.
    /// <para>
    /// Exposed because it is the evidence behind the endpoint fallback standing down: the fallback
    /// is safe only for as long as it can see what the host already serves, and a framework change
    /// that stopped it seeing them would otherwise show up as an ambiguous match on every checkout.
    /// <see cref="CheckoutWiringTests"/> asserts the list is populated, so that failure arrives as
    /// a sentence instead.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ObservedRoutes => _observedRoutes;

    /// <summary>A browser: its own cookie jar, so two clients are genuinely two visitors.</summary>
    public HttpClient NewBrowser() => CreateClient();

    /// <summary>
    /// A client with no cookie jar, for the retrieval-link tests that need a request carrying no
    /// session at all rather than one carrying a fresh session.
    /// </summary>
    public HttpClient NewRawClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = ClientOptions.BaseAddress,
        HandleCookies = false,
    });

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<VelaCommerceDbContext>>();
            services.RemoveAll<DbContextOptions<VelaCommerceDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<VelaCommerceDbContext>();

            // Registered through AddDbContext, not a hand-rolled factory, so the container still
            // supplies the optional ICurrentDemoSession the tenancy filter reads.
            //
            // Retries are configured exactly as the real host configures them, and that is not
            // decoration. A retrying execution strategy refuses user-initiated transactions unless
            // the whole unit is handed to it, so checkout's two transactions are shaped around its
            // presence; a test host without it would exercise a code path the deployment does not
            // have and would say nothing about the one it does.
            services.AddDbContext<VelaCommerceDbContext>(options => options.UseNpgsql(
                _connectionString,
                npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)));

            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

            ComposeMissingServices(services);

            services.AddSingleton<IStartupFilter>(
                new CheckoutSurfaceStartupFilter(_composedForYou, _observedRoutes));
        });
    }

    /// <summary>
    /// Adds the two registrations checkout resolves, if — and only if — the application has not
    /// already added them. Both are <c>TryAdd</c>-based upstream, so the calls are inert once
    /// Program.cs makes them; the explicit check exists to report the gap, not to avoid a
    /// duplicate.
    /// </summary>
    private void ComposeMissingServices(IServiceCollection services)
    {
        if (!services.Any(service => service.ServiceType == typeof(TimeProvider)))
        {
            _composedForYou.Add("builder.Services.AddCheckout();");
            services.AddCheckout();
        }

        if (!services.Any(service => service.ServiceType == typeof(IPaymentGateway)))
        {
            _composedForYou.Add(
                "builder.Services.AddPaymentSimulator(builder.Configuration, builder.Environment.IsDevelopment());");

            // The test secret is handed over directly rather than left to the environment
            // variable, so this registration does not depend on ambient state and the simulator
            // does not log its "you are signing with the committed development secret" warning at
            // every host build. isDevelopment: true only says a test process may sign with a test
            // key; the host itself stays Production.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{PaymentSimulatorOptions.SectionName}:{nameof(PaymentSimulatorOptions.SigningSecret)}"] =
                        TestSigningSecret,
                })
                .Build();

            services.AddPaymentSimulator(configuration, isDevelopment: true);
        }
    }

    /// <summary>
    /// Maps the checkout endpoints after the application's own pipeline, but only when the
    /// application has not mapped them itself.
    /// <para>
    /// The middleware is appended <em>after</em> <c>next(app)</c> rather than before it, which is
    /// what makes it correct rather than merely convenient. Checkout has to run downstream of
    /// <c>UseDemoSession</c> — an order needs an owner — and everything registered before
    /// <c>next</c> runs upstream of every middleware the application installs. Appending puts these
    /// endpoints where the host would have put them: behind the exception handler, behind the
    /// session, and reachable only by a request the application's own routing did not match.
    /// </para>
    /// <para>
    /// The check is the important half. Two identical route patterns in one matcher are an
    /// ambiguous match — an exception on every checkout — so the day <c>Program.cs</c> maps these
    /// endpoints this filter must add nothing at all. It asks the application's own endpoint route
    /// builder what it already serves, which is the same collection the router will match against.
    /// </para>
    /// </summary>
    private sealed class CheckoutSurfaceStartupFilter(List<string> composedForYou, List<string> observedRoutes)
        : IStartupFilter
    {
        /// <summary>The well-known key <c>UseRouting</c> stores its route builder under.</summary>
        private const string EndpointRouteBuilderKey = "__EndpointRouteBuilder";

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                next(app);

                observedRoutes.AddRange(RoutesOf(app));

                if (observedRoutes.Any(route => route.Contains("checkout", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                composedForYou.Add("app.MapCheckoutEndpoints();");

                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapCheckoutEndpoints());
            };

        /// <summary>
        /// What the application already serves.
        /// <para>
        /// Read from the route pattern rather than from a service or a marker type, because mapping
        /// an endpoint leaves no trace in the service collection — the omission this guards against
        /// is invisible everywhere except in the routing table.
        /// </para>
        /// </summary>
        private static IEnumerable<string> RoutesOf(IApplicationBuilder app) =>
            app.Properties.TryGetValue(EndpointRouteBuilderKey, out var value) && value is IEndpointRouteBuilder routes
                ? routes.DataSources
                    .SelectMany(source => source.Endpoints)
                    .OfType<RouteEndpoint>()
                    .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
                : [];
    }
}
