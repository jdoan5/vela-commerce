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
using Microsoft.Extensions.Logging;

using VelaCommerce.Api.Endpoints;
using VelaCommerce.Infrastructure.Fulfilment;
using VelaCommerce.Infrastructure.Messaging;
using VelaCommerce.Infrastructure.Payments;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The real API host with the whole settlement loop attached: checkout writes outbox rows, the
/// real <see cref="OutboxDispatcher"/> delivers them over real HTTP into the real
/// <c>/api/payments/webhook</c>, and the real <see cref="OrderTimelineWorker"/> moves what the
/// webhook paid.
///
/// <para>
/// It is a third near-twin of <see cref="DemoSessionHost"/> and <see cref="CheckoutHost"/>, and
/// deliberately does not subclass either — both are sealed, and the substitutions below have to
/// happen in the same <c>ConfigureTestServices</c> callback as the ones they make. The three
/// shared substitutions carry exactly the reasoning <see cref="CheckoutHost"/> documents at
/// length: the connection string is replaced rather than configured so an ambient
/// <c>VELA_DB_CONNECTION</c> cannot redirect the suite at the developer's own database; the Data
/// Protection key ring is ephemeral so real cryptography runs while nothing is written to the
/// developer's home directory; and the environment is Production because that is what the shared
/// demo runs as. Read that file for the long form.
/// </para>
///
/// <para>
/// <b>Four things are specific to this host, and each one is load-bearing.</b>
/// </para>
///
/// <para>
/// <b>1. It maps the settlement receiver, because <c>Program.cs</c> does not yet.</b> Same
/// mechanism <see cref="CheckoutHost"/> uses and for the same reason — a suite driving an
/// unmapped endpoint is a wall of 404s that says nothing about whether exactly-once delivery
/// works — and the same standing-down check, so the day the host maps it itself this filter adds
/// nothing and there is no ambiguous route match. Every gap it fills is recorded in
/// <see cref="ComposedForYou"/>, which <see cref="SettlementWiringTests"/> reports as the exact
/// lines <c>Program.cs</c> is missing.
/// </para>
///
/// <para>
/// <b>2. It replaces <see cref="PaymentSimulatorOptions"/> rather than configuring it.</b>
/// <c>AddPaymentSimulator</c> reads configuration while services are being registered — before
/// <c>ConfigureTestServices</c> runs — so the only configuration channel guaranteed to arrive in
/// time is an environment variable, and an environment variable is process-wide: setting one here
/// would reach into every other host in the test run, including <see cref="CheckoutHost"/>'s.
/// Replacing the registered options object is local, explicit, and hostage to nothing ambient.
/// It buys two things this suite cannot do without: a signing secret that is not the committed
/// development default (<see cref="PaymentSimulatorOptions.AssertUsable"/> refuses that one on
/// money paths, and the webhook receiver is a money path), and a settlement delay measured in
/// milliseconds instead of the three seconds a reviewer wants to watch.
/// </para>
///
/// <para>
/// <b>3. Neither background loop runs on a timer.</b> Both are registered — so the composition is
/// the real one — and both are configured <c>Enabled=false</c>, so their <c>ExecuteAsync</c>
/// returns immediately and every sweep in this suite is one a test asked for. That is not merely
/// for determinism. The fixture's PostgreSQL container is shared by the whole assembly, so a
/// dispatcher on a one-second timer would deliver outbox rows left behind by other test classes —
/// rows signed with <see cref="CheckoutHost"/>'s different secret — into this host, which would
/// refuse them, retry them and eventually abandon them. A test suite that quietly corrupts its
/// neighbours' fixtures is worse than a slow one.
/// </para>
///
/// <para>
/// <b>4. The dispatcher posts through the <see cref="TestServer"/>, not through a socket.</b>
/// There is no listener in a <see cref="WebApplicationFactory"/>, so
/// <c>OutboxOptions.ResolveReceiverUrl</c> would discover no origin and the dispatcher would
/// decline to start — correctly, and uselessly for a test. The handler is therefore the test
/// server's own, resolved lazily: <see cref="WebApplicationFactory{TEntryPoint}.Server"/> builds
/// and starts the host on first access, so touching it while the host is starting would be
/// re-entrant. Everything above the handler is production code — the real dispatcher's claim
/// query, the real delivery client's byte array and header, the real endpoint filter pipeline.
/// </para>
/// </summary>
public sealed class SettlementHost : WebApplicationFactory<Program>
{
    /// <summary>
    /// The shared secret this host signs and verifies settlement notifications with.
    /// <para>
    /// Not a secret in any meaningful sense — it authenticates simulated webhooks inside one test
    /// process — and written down rather than generated so a signature failure is reproducible.
    /// Deliberately different from <see cref="CheckoutHost.TestSigningSecret"/>: a notification
    /// enqueued by one host must not verify at the other's receiver, which is what keeps the
    /// shared container's leftover outbox rows out of these assertions.
    /// </para>
    /// </summary>
    public const string SigningSecret = "vela-settlement-tests-hmac-key-9f3a1c7e5b2d4086af1c9e3b7d5a2064";

    /// <summary>
    /// How long after authorization the simulator schedules a deferred settlement.
    /// <para>
    /// The shipped default is three seconds, which is a stage direction for a human watching a
    /// spinner and dead time in a test. It stays non-zero on purpose: a zero delay would make
    /// every message due the instant it was written and would quietly stop proving that
    /// <c>deliver_after</c> is honoured at all.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SettlementDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Where the dispatcher posts. The receiver's own route constant, so a rename breaks here first.</summary>
    public static readonly Uri ReceiverUrl = new("https://localhost" + WebhookEndpoints.SettlementRoute);

    private readonly string _connectionString;
    private readonly List<string> _composedForYou = [];
    private readonly List<string> _observedRoutes = [];

    private readonly Lock _gate = new();
    private OutboxDeliveryClient? _deliveryClient;
    private OutboxDispatcher? _dispatcher;
    private LazyTestServerHandler? _handler;

    /// <summary>Binds a host to the container the fixture has already started and migrated.</summary>
    /// <param name="connectionString"><see cref="PostgresFixture.ConnectionString"/>.</param>
    public SettlementHost(string connectionString)
    {
        _connectionString = connectionString;

        // https on a server that never negotiates TLS, for the reason DemoSessionHost gives: the
        // demo session cookie ships with Secure set, and CookieContainer would accept it over http
        // and then decline to send it back, so every shopper would look like a new visitor.
        ClientOptions.BaseAddress = new Uri("https://localhost/");
    }

    /// <summary>
    /// The composition this host had to supply because the application did not. Empty is the
    /// goal; anything in it is a line missing from <c>Program.cs</c>. Populated while the host is
    /// built, so read it only after a client has been created.
    /// </summary>
    public IReadOnlyList<string> ComposedForYou => _composedForYou;

    /// <summary>Every route pattern the application had mapped by the time the pipeline was built.</summary>
    public IReadOnlyList<string> ObservedRoutes => _observedRoutes;

    /// <summary>A shopper's browser: its own cookie jar, so two clients are genuinely two visitors.</summary>
    public HttpClient NewBrowser() => CreateClient();

    /// <summary>
    /// A client with no cookie jar and no session, which is what a payment gateway is. Used for
    /// every hand-delivered webhook, so nothing in this suite can pass because a settlement
    /// happened to carry a demo session the tenancy filter would have matched.
    /// </summary>
    public HttpClient NewGateway() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = ClientOptions.BaseAddress,
        HandleCookies = false,
    });

    /// <summary>
    /// The real dispatcher, wired to deliver into this host's own pipeline. One instance for the
    /// life of the host, because it owns an <see cref="HttpClient"/>.
    /// </summary>
    public OutboxDispatcher Dispatcher
    {
        get
        {
            lock (_gate)
            {
                if (_dispatcher is not null)
                {
                    return _dispatcher;
                }

                // Resolved through a factory delegate rather than captured now: touching Server
                // builds and starts the host, and this property is read from a test, by which time
                // it is up. See the class remarks.
                _handler = new LazyTestServerHandler(() => Server.CreateHandler());

                _deliveryClient = new OutboxDeliveryClient(
                    Services.GetRequiredService<OutboxOptions>(),
                    _handler);

                _dispatcher = new OutboxDispatcher(
                    Services.GetRequiredService<IServiceScopeFactory>(),
                    Services.GetRequiredService<OutboxOptions>(),
                    _deliveryClient,
                    Services.GetRequiredService<TimeProvider>(),
                    Services.GetRequiredService<ILogger<OutboxDispatcher>>());

                return _dispatcher;
            }
        }
    }

    /// <summary>
    /// The timeline worker the container built, not a second one. Resolving the registered hosted
    /// service rather than constructing one keeps this test honest about the composition:
    /// if <c>AddOrderTimeline</c> stopped registering the worker, this throws rather than quietly
    /// exercising an instance nothing deploys.
    /// </summary>
    public OrderTimelineWorker Timeline =>
        Services.GetServices<IHostedService>().OfType<OrderTimelineWorker>().First();

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

            // Retries configured exactly as the real host configures them. A retrying execution
            // strategy refuses user-initiated transactions unless the whole unit is handed to it,
            // and both the dispatcher and the webhook receiver are shaped around its presence — a
            // test host without it would exercise a code path the deployment does not have.
            services.AddDbContext<VelaCommerceDbContext>(options => options.UseNpgsql(
                _connectionString,
                npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)));

            // THE REAPER IS SILENCED IN EVERY TEST HOST, FOR THE REASON SettlementHost STATES
            // ABOUT THE OTHER TWO WORKERS: every sweep in this suite should be one a test asked
            // for. It is registered — so the composition stays the real one — and its timer loop
            // returns immediately.
            //
            // Without this it swept the SHARED container on the system clock, on boot and every
            // minute after, from all three hosts at once. Reservations made by other classes
            // expire fifteen minutes out so they were usually safe, but any test that backdates
            // expires_at to make a reservation lapse was racing an uncontrolled third writer.
            services.RemoveAll<ReservationReaperOptions>();
            services.AddSingleton(new ReservationReaperOptions { Enabled = false });

            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

            // The simulator's options, replaced rather than configured. See remark 2 on the class.
            // RemoveAll first because AddPaymentSimulator registers with TryAdd, so the host's
            // own object is already in the collection by the time this callback runs.
            services.RemoveAll<PaymentSimulatorOptions>();
            services.AddSingleton(new PaymentSimulatorOptions
            {
                SigningSecret = SigningSecret,
                SettlementDelay = SettlementDelay,

                // Left at the shipped five minutes rather than shortened. The expired-signature
                // test signs ten days out of date, so it does not need a small window — and a
                // small window here would make every other test a hostage to how long a container
                // takes to answer.
                SignatureTolerance = TimeSpan.FromMinutes(5),

                // Off, as it is outside Development. Every checkout in this suite names its
                // scenario explicitly, so a total whose trailing cents happen to read as a magic
                // amount must not quietly select a different one.
                RecogniseMagicAmounts = false,
            });

            ComposeMissingServices(services);

            services.AddSingleton<IStartupFilter>(
                new SettlementSurfaceStartupFilter(_composedForYou, _observedRoutes));
        });
    }

    /// <summary>
    /// Registers the outbox and the order timeline, and records which of them the application had
    /// not registered for itself.
    /// <para>
    /// Both are added unconditionally, because this suite needs its own options either way — a
    /// dispatcher on a timer would deliver other test classes' outbox rows into this host, and a
    /// timeline on a timer would advance their orders. What the presence check produces is not the
    /// registration but the <em>report</em>: the exact lines <c>Program.cs</c> is missing, which is
    /// the whole value of <see cref="SettlementWiringTests"/>.
    /// </para>
    /// <para>
    /// The <c>RemoveAll</c> calls matter on the day the application does compose these. Both
    /// <c>Add*</c> methods register their options with <c>TryAdd</c>, so without removing first the
    /// host's own object would win and this suite would silently start running two live background
    /// loops against a shared container.
    /// </para>
    /// </summary>
    private void ComposeMissingServices(IServiceCollection services)
    {
        if (!services.Any(service => service.ServiceType == typeof(OutboxOptions)))
        {
            _composedForYou.Add("builder.Services.AddOutbox(builder.Configuration);");
        }

        if (!services.Any(service => service.ServiceType == typeof(OrderTimelineOptions)))
        {
            _composedForYou.Add("builder.Services.AddOrderTimeline(builder.Configuration);");
        }

        var settings = BackgroundSettings();

        services.RemoveAll<OutboxOptions>();
        services.RemoveAll<OrderTimelineOptions>();

        services.AddOutbox(settings);
        services.AddOrderTimeline(settings);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dispatcher?.Dispose();
            _deliveryClient?.Dispose();
            _handler?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Configuration for the two background workers: both switched off, and the timeline's dwells
    /// collapsed to zero.
    /// <para>
    /// Zero dwells make the timeline a pure "advance everything that is due", which is what lets a
    /// test assert on a transition instead of on a stopwatch. The schedule itself is derived from
    /// <c>PaidAt</c> and is unit-tested arithmetic; what an integration test can prove that no
    /// unit test can is that the claim, the transition and the stock UPDATE agree with PostgreSQL,
    /// and none of that changes with the length of a dwell.
    /// </para>
    /// </summary>
    private static IConfiguration BackgroundSettings() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{OutboxOptions.SectionName}:{nameof(OutboxOptions.Enabled)}"] = "false",
                [$"{OutboxOptions.SectionName}:{nameof(OutboxOptions.ReceiverUrl)}"] = ReceiverUrl.ToString(),

                // Generous, because a sweep claims whatever is due across the whole shared
                // container and a batch that filled with another class's leftovers could leave
                // this test's own message unclaimed.
                [$"{OutboxOptions.SectionName}:{nameof(OutboxOptions.BatchSize)}"] = "50",

                [$"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.Enabled)}"] = "false",
                [$"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.PaidDwell)}"] = "00:00:00",
                [$"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.PackedDwell)}"] = "00:00:00",
                [$"{OrderTimelineOptions.SectionName}:{nameof(OrderTimelineOptions.BatchSize)}"] = "200",
            })
            .Build();

    /// <summary>
    /// Maps the settlement receiver after the application's own pipeline, and only when the
    /// application has not mapped it itself.
    /// <para>
    /// Appended after <c>next(app)</c> rather than before it, which is what makes it correct
    /// rather than merely convenient: everything registered before <c>next</c> runs upstream of
    /// every middleware the application installs, and this endpoint belongs behind the exception
    /// handler like every other. The standing-down check is the important half — two identical
    /// route patterns in one matcher are an ambiguous match, so the day <c>Program.cs</c> maps
    /// this endpoint this filter must add nothing at all.
    /// </para>
    /// </summary>
    private sealed class SettlementSurfaceStartupFilter(List<string> composedForYou, List<string> observedRoutes)
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

                if (observedRoutes.Any(route =>
                        route.Contains("payments/webhook", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                composedForYou.Add("app.MapWebhookEndpoints();");

                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapWebhookEndpoints());
            };

        /// <summary>
        /// What the application already serves, read from the routing table rather than from a
        /// service or a marker type — mapping an endpoint leaves no trace in the service
        /// collection, so the routing table is the only place the omission is visible.
        /// </summary>
        private static IEnumerable<string> RoutesOf(IApplicationBuilder app) =>
            app.Properties.TryGetValue(EndpointRouteBuilderKey, out var value) && value is IEndpointRouteBuilder routes
                ? routes.DataSources
                    .SelectMany(source => source.Endpoints)
                    .OfType<RouteEndpoint>()
                    .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
                : [];
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that fetches the real one on first use.
    /// <para>
    /// <see cref="WebApplicationFactory{TEntryPoint}.Server"/> builds and starts the host the
    /// first time it is touched, so a handler captured while services are being registered would
    /// be re-entrant on the host that is being built. Deferring to the first request moves that
    /// touch to a point where the host is up.
    /// </para>
    /// </summary>
    private sealed class LazyTestServerHandler(Func<HttpMessageHandler> resolve) : HttpMessageHandler
    {
        private readonly Lazy<HttpMessageInvoker> _invoker =
            new(() => new HttpMessageInvoker(resolve(), disposeHandler: true));

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _invoker.Value.SendAsync(request, cancellationToken);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && _invoker.IsValueCreated)
            {
                _invoker.Value.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
