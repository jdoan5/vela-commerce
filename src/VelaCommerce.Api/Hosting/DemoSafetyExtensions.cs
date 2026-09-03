using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using VelaCommerce.Api.Endpoints;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Hosting;

/// <summary>
/// Everything that makes this demo safe to leave running on the public internet with nobody
/// watching it: rate limits on the paths that write, row caps so one visitor cannot fill the
/// database, and the security headers that go on every response.
/// <para>
/// They are composed together because they are one decision — "a stranger may use this, and a
/// stranger may abuse it" — and because wiring them separately is how one of the three ends up
/// forgotten in a host that has the other two.
/// </para>
/// </summary>
public static class DemoSafetyExtensions
{
    /// <summary>
    /// Log category. <c>ILogger&lt;T&gt;</c> is unavailable because a static class cannot be a type
    /// argument, and inventing a marker type purely to satisfy the generic would be worse than
    /// naming the category once. Matches the convention in <c>CheckoutEndpoints</c>.
    /// </summary>
    private const string LogCategory = "VelaCommerce.Api.Hosting.DemoSafety";

    /// <summary>
    /// Registers the demo's abuse controls. Call from the composition root as
    /// <c>builder.Services.AddDemoSafety(builder.Configuration);</c>.
    /// <para>
    /// Configuration is a parameter rather than a resolved dependency for the same reason
    /// <c>AddOutbox</c> and <c>AddPaymentSimulator</c> take it: the numbers have to be readable
    /// while services are being registered, before any provider exists.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDemoSafety(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolved lazily so that a real logger exists by the time the values are read, which is
        // what lets a mistyped limit be reported rather than silently replaced.
        services.TryAddSingleton(provider => DemoSafetySettings.Read(
            configuration,
            provider.GetService<ILoggerFactory>()?.CreateLogger(LogCategory)));

        // Kestrel announces itself on every response by default. It is not a vulnerability, it is
        // free reconnaissance, and turning it off costs one line.
        services.Configure<KestrelServerOptions>(static options => options.AddServerHeader = false);

        services.AddRateLimiter(static _ => { });

        // Post-configured rather than configured inline, so the policy can be built from the
        // settings object above instead of from a second, unlogged read of configuration.
        services.AddOptions<RateLimiterOptions>()
            .Configure<DemoSafetySettings>(ConfigureRateLimiter);

        return services;
    }

    /// <summary>
    /// Installs the security headers, the rate limiter and the per-session row caps.
    /// <para>
    /// <strong>Call this immediately after <c>app.UseDemoSession();</c></strong> and before the
    /// endpoints. The position is load-bearing in both directions. After the session, because the
    /// rate limiter partitions by visitor and the row caps count that visitor's rows — placed
    /// earlier, both would fall back to treating everyone as one anonymous caller. Before the
    /// endpoints and before <c>MapStorefront</c>, because the headers are attached to a response
    /// that has not started yet and a refusal has to happen instead of the work, not after it.
    /// </para>
    /// <para>
    /// Takes <see cref="WebApplication"/> rather than <see cref="IApplicationBuilder"/> because it
    /// needs the environment, the configuration and a logger to compose the Content-Security-Policy
    /// from the storefront that is actually on disk, and because — like <c>MapStorefront</c> — it
    /// is one feature that must not be half-installed.
    /// </para>
    /// </summary>
    public static WebApplication UseDemoSafety(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

        // Located once, here — the storefront probe is a handful of file checks and belongs at
        // startup. The policy itself is composed on the first document response and recomposed
        // whenever the shell changes underneath the host, because its script hashes describe a file
        // that a storefront rebuild rewrites; see SecurityHeaderPolicySource.
        var headers = SecurityHeaders.Build(app.Environment, app.Configuration, logger);

        app.Use(async (context, next) =>
        {
            // Registered rather than written, so the policy is chosen from the Content-Type the
            // response ends up with — which nothing this early in the pipeline knows.
            context.Response.OnStarting(static state =>
            {
                var (httpContext, source) = ((HttpContext, SecurityHeaderPolicySource))state;
                SecurityHeaders.Apply(httpContext, source);
                return Task.CompletedTask;
            }, (context, headers));

            await next(context);
        });

        var settings = app.Services.GetService<DemoSafetySettings>();

        if (settings is null)
        {
            // The one composition mistake this file can be made to survive: UseDemoSafety without
            // AddDemoSafety. UseRateLimiter would throw here and take the whole host down with it —
            // including the build-time OpenAPI generation, which runs this exact entry point — so
            // it says so loudly and installs what it still can. The headers above are already on.
            logger.LogError(
                "app.UseDemoSafety() was called without builder.Services.AddDemoSafety(configuration). "
                + "Security headers are installed, but rate limiting and the per-session row caps "
                + "are NOT. Add the registration before deploying this anywhere public.");

            return app;
        }

        logger.LogInformation(
            "Demo safety is on. Writes: {WriteBurst} burst then {WriteRate}/s per session, "
            + "{AddressBurst} burst then {AddressRate}/s per address; reads: {ReadBurst} burst then "
            + "{ReadRate}/s per address; caps: {Carts} carts, {Lines} lines per cart, {Orders} orders "
            + "per session. The settlement receiver keeps its own limiter and is exempt from these.",
            settings.WriteBurstPerSession,
            settings.WritesPerSecondPerSession,
            settings.WriteBurstPerAddress,
            settings.WritesPerSecondPerAddress,
            settings.ReadBurstPerAddress,
            settings.ReadsPerSecondPerAddress,
            settings.Quotas.MaxCartsPerSession,
            settings.Quotas.MaxLinesPerCart,
            settings.Quotas.MaxOrdersPerSession);

        app.UseRateLimiter();

        var quotas = settings.Quotas;

        app.Use(async (context, next) =>
        {
            var refusal = await DemoQuotas.EvaluateAsync(context, quotas, context.RequestAborted);

            if (refusal is not null)
            {
                context.Response.Headers.CacheControl = "no-store";
                await refusal.ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        return app;
    }

    /// <summary>
    /// Builds the global limiter and the shape of a 429.
    /// <para>
    /// <strong>A global limiter, not a named policy, and that is a constraint rather than a
    /// preference.</strong> A policy has to be attached with <c>RequireRateLimiting</c> on each
    /// endpoint, and the cart and checkout groups are composed in files this slice does not own.
    /// The global limiter reaches every request without touching a single registration — and it is
    /// the safer default anyway, since an endpoint added next year is covered by construction
    /// rather than by somebody remembering.
    /// </para>
    /// <para>
    /// <strong>Where the limiters live.</strong> Each <see cref="PartitionedRateLimiter"/> is
    /// created once here and owned by <see cref="RateLimiterOptions"/>, which is a singleton
    /// disposed with the host. Nothing constructs a limiter per request: the partitioner returns a
    /// <em>description</em> of the partition it wants, and the framework creates the underlying
    /// limiter the first time a key is seen, caches it, and reclaims it once it has been idle.
    /// That is the whole reason for using the built-in partitioning rather than a dictionary of
    /// limiters — a hand-rolled cache keyed by session id is a memory leak with an eviction policy
    /// somebody has to remember to write, and one visitor per key is a lot of keys.
    /// </para>
    /// <para>
    /// <c>AutoReplenishment = false</c> on every bucket is part of the same point.
    /// <see cref="PartitionedRateLimiter"/> runs one replenishment timer for all of its partitions;
    /// leaving auto-replenishment on would give every partition a timer of its own, which is one
    /// system timer per visitor.
    /// </para>
    /// </summary>
    private static void ConfigureRateLimiter(RateLimiterOptions options, DemoSafetySettings settings)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = async (context, _) =>
        {
            var response = context.HttpContext.Response;

            // RFC 9110 wants a 429 to say when to come back. Fixed and token-bucket limiters both
            // report it as lease metadata; the fallback is the replenishment period, which is the
            // true answer for a token bucket that has just run dry.
            var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                : settings.ReplenishmentSeconds;

            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

            // A throttled answer is about this caller at this moment. Nothing may cache it.
            response.Headers.CacheControl = "no-store";

            await Results.Problem(
                    title: "Too many requests",
                    detail: "This is a shared public demo, so writes are rate limited per visitor. "
                            + "Nothing was changed. Wait for the number of seconds in the "
                            + "Retry-After header and try again - browsing, searching and filtering "
                            + "the catalog are never rate limited, because they never touch the API.",
                    statusCode: StatusCodes.Status429TooManyRequests)
                .ExecuteAsync(context.HttpContext);
        };

        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => WritesPerSession(context, settings)),
            PartitionedRateLimiter.Create<HttpContext, string>(context => WritesPerAddress(context, settings)),
            PartitionedRateLimiter.Create<HttpContext, string>(context => ReadsPerAddress(context, settings)));
    }

    /// <summary>
    /// The main control: how much one visitor may write.
    /// <para>
    /// A token bucket rather than a fixed window, because the shape of real use is bursty and the
    /// shape of abuse is not. A shopper leaning on the quantity stepper fires a dozen PATCHes in
    /// three seconds and must not be told off for it; a script writing steadily for an hour must
    /// be. A bucket gives the first one its burst and holds the second to the refill rate, which a
    /// fixed window does neither of — it refuses the burst and then allows a double burst across
    /// the window boundary.
    /// </para>
    /// <para>
    /// No queue. A queued write is a shopper watching a spinner for a request the server has
    /// already decided to deprioritise; an immediate 429 with a Retry-After is both faster and
    /// honest.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> WritesPerSession(HttpContext context, DemoSafetySettings settings)
    {
        if (!IsThrottledApiRequest(context) || !IsWrite(context))
        {
            return RateLimitPartition.GetNoLimiter(NoPartition);
        }

        // The session id, never the raw cookie: the cookie is a credential and a partition key ends
        // up in memory keyed by exactly that string. A visitor whose very first request is a write
        // has no session yet and falls back to their address, which is the correct grouping for an
        // anonymous writer — and is why a cookie-less flood cannot get a fresh bucket per request.
        var key = context.RequestServices.GetService<ICurrentDemoSession>()?.SessionId is { } sessionId
            ? $"session:{sessionId:N}"
            : $"anonymous:{AddressKey(context) ?? "unknown"}";

        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = settings.WriteBurstPerSession,
            TokensPerPeriod = settings.WriteTokensPerPeriodPerSession,
            ReplenishmentPeriod = settings.ReplenishmentPeriod,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        });
    }

    /// <summary>
    /// The backstop against the obvious way around a per-session limit: farming sessions.
    /// <para>
    /// A new session costs one GET, so an attacker can have as many buckets as they like. This
    /// second limiter is what makes that not worth doing. It is set well above the per-session
    /// figure so that a genuinely shared address — an office, a university, a conference wifi —
    /// does not throttle one person because of another.
    /// </para>
    /// <para>
    /// <strong>Loopback and unknown addresses are exempt, deliberately.</strong> Every test host in
    /// this repository reports no remote address at all, and the CI job that drives the Bruno
    /// collection connects over 127.0.0.1; without the exemption, fifty in-process shoppers racing
    /// for five units would share one bucket and the concurrency suite would fail as a rate-limit
    /// problem dressed up as a checkout bug. <strong>The deploy-phase consequence is worth writing
    /// down: behind a reverse proxy every request arrives from the proxy</strong>, so this limiter
    /// would either see one address for the whole world or, if the proxy is local, be switched off
    /// entirely. Either way it needs <c>UseForwardedHeaders</c> with a known-proxies list before it
    /// means anything in production. Until then the per-session limiter and the row caps are the
    /// controls that actually bind.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> WritesPerAddress(HttpContext context, DemoSafetySettings settings)
    {
        if (!IsThrottledApiRequest(context) || !IsWrite(context) || AddressKey(context) is not { } address)
        {
            return RateLimitPartition.GetNoLimiter(NoPartition);
        }

        return RateLimitPartition.GetTokenBucketLimiter($"write:{address}", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = settings.WriteBurstPerAddress,
            TokensPerPeriod = settings.WriteTokensPerPeriodPerAddress,
            ReplenishmentPeriod = settings.ReplenishmentPeriod,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        });
    }

    /// <summary>
    /// A ceiling on API reads, which is a much smaller concern than it looks.
    /// <para>
    /// <strong>Nothing on the first-paint path passes through here.</strong> The storefront browses,
    /// searches, filters and sorts entirely from a static <c>catalog.snapshot.json</c> served as a
    /// file, and the WebAssembly runtime's own assets are files too — none of them are under
    /// <c>/api</c>, so none of them can be throttled by this limiter however hard somebody reloads.
    /// That is the property that makes a read limit safe to have at all: a limiter that could
    /// refuse a page load would be a worse outage than the one it prevents.
    /// </para>
    /// <para>
    /// What is left under <c>/api</c> for a GET is the cart, an order and the catalog endpoints
    /// used to turn a SKU into a variant id — a handful of calls per visitor. The ceiling is set
    /// far above that, and exempts loopback for the reason the write limiter does.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> ReadsPerAddress(HttpContext context, DemoSafetySettings settings)
    {
        if (!IsThrottledApiRequest(context) || IsWrite(context) || AddressKey(context) is not { } address)
        {
            return RateLimitPartition.GetNoLimiter(NoPartition);
        }

        return RateLimitPartition.GetTokenBucketLimiter($"read:{address}", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = settings.ReadBurstPerAddress,
            TokensPerPeriod = settings.ReadTokensPerPeriodPerAddress,
            ReplenishmentPeriod = settings.ReplenishmentPeriod,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        });
    }

    /// <summary>
    /// The one partition key that means "do not limit this". A single shared constant because
    /// <see cref="PartitionedRateLimiter"/> keys its cache by this value, and a fresh string per
    /// request would be a fresh no-op partition per request.
    /// </summary>
    private const string NoPartition = "unlimited";

    /// <summary>Paths under this prefix are the only ones any limiter here considers.</summary>
    private static readonly PathString ApiPrefix = new("/api");

    /// <summary>
    /// The settlement receiver, which is exempt.
    /// <para>
    /// It already owns a fixed-window limiter sized against the outbox dispatcher's own ceiling,
    /// and it is reached by a signed, machine-driven sender rather than by a browser. Throttling it
    /// here would silently delay settlements — the dispatcher would back off and retry, and the
    /// visible symptom would be orders sitting at Pending for no reason anyone could see. The
    /// exemption is safe precisely because that endpoint is not unprotected: it verifies an HMAC
    /// over the exact bytes of the body before it does anything at all.
    /// </para>
    /// </summary>
    private static readonly PathString WebhookPath = new("/api/payments/webhook");

    /// <summary>
    /// Whether this request is one the demo limiters have any business looking at: under
    /// <c>/api</c>, and not the settlement receiver.
    /// </summary>
    private static bool IsThrottledApiRequest(HttpContext context)
    {
        var path = context.Request.Path;

        return path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase)
               && !path.StartsWithSegments(WebhookPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the request can change anything. Anything that is not a safe method counts, so a
    /// verb nobody has thought of yet is limited rather than exempt.
    /// </summary>
    private static bool IsWrite(HttpContext context)
    {
        var method = context.Request.Method;

        return !HttpMethods.IsGet(method)
               && !HttpMethods.IsHead(method)
               && !HttpMethods.IsOptions(method);
    }

    /// <summary>
    /// The caller's address as a partition key, or null when there is nothing usable to key on.
    /// <para>
    /// Loopback is null on purpose — see <see cref="WritesPerAddress"/>. An IPv6 address is
    /// normalised by <see cref="IPAddress.ToString"/>, and no attempt is made to group a /64 the
    /// way a production limiter would: on a demo that would trade a real risk of throttling
    /// unrelated visitors for a theoretical one of an attacker rotating addresses they already own.
    /// </para>
    /// </summary>
    private static string? AddressKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;

        if (address is null || IPAddress.IsLoopback(address))
        {
            return null;
        }

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }
}

/// <summary>
/// The numbers behind the demo's abuse controls, read once and logged at startup.
/// <para>
/// Rates are expressed as "tokens per replenishment period" because that is what a token bucket
/// takes, and as a per-second figure in the log because that is what a person reads. One period is
/// shared by every bucket so the single replenishment timer serves them all on the same tick.
/// </para>
/// </summary>
internal sealed record DemoSafetySettings
{
    /// <summary>Configuration section for the limiters. The row caps have their own, under <c>Demo:Quotas</c>.</summary>
    public const string SectionName = "Demo:RateLimits";

    /// <summary>How often every bucket refills. Ten seconds: long enough that one timer tick is cheap, short enough that a shopper who hits a limit is not left waiting.</summary>
    public TimeSpan ReplenishmentPeriod { get; private init; } = TimeSpan.FromSeconds(10);

    /// <summary>The Retry-After fallback, in whole seconds, when a lease reports no metadata of its own.</summary>
    public int ReplenishmentSeconds => Math.Max(1, (int)ReplenishmentPeriod.TotalSeconds);

    /// <summary>Writes one visitor may make back to back before the refill rate binds.</summary>
    public int WriteBurstPerSession { get; private init; }

    /// <summary>Writes returned to one visitor's bucket each period.</summary>
    public int WriteTokensPerPeriodPerSession { get; private init; }

    /// <summary>Writes one address may make back to back, across however many sessions it holds.</summary>
    public int WriteBurstPerAddress { get; private init; }

    /// <summary>Writes returned to one address's bucket each period.</summary>
    public int WriteTokensPerPeriodPerAddress { get; private init; }

    /// <summary>API reads one address may make back to back. Static files and the catalog snapshot are not API reads.</summary>
    public int ReadBurstPerAddress { get; private init; }

    /// <summary>API reads returned to one address's bucket each period.</summary>
    public int ReadTokensPerPeriodPerAddress { get; private init; }

    /// <summary>The per-session row caps, which bound accumulation rather than rate.</summary>
    public DemoQuotaOptions Quotas { get; private init; } = DemoQuotaOptions.Defaults;

    /// <summary>Sustained writes per second for one visitor, for the startup log.</summary>
    public double WritesPerSecondPerSession => WriteTokensPerPeriodPerSession / ReplenishmentPeriod.TotalSeconds;

    /// <summary>Sustained writes per second for one address, for the startup log.</summary>
    public double WritesPerSecondPerAddress => WriteTokensPerPeriodPerAddress / ReplenishmentPeriod.TotalSeconds;

    /// <summary>Sustained API reads per second for one address, for the startup log.</summary>
    public double ReadsPerSecondPerAddress => ReadTokensPerPeriodPerAddress / ReplenishmentPeriod.TotalSeconds;

    /// <summary>
    /// The shipped numbers.
    /// <para>
    /// Sized against what the demo actually does rather than against a round number. Forty writes
    /// back to back covers every burst a person can produce with a mouse — the worst case measured
    /// is a leaned-on quantity stepper, which the storefront already serialises — and two a second
    /// sustained is roughly twenty times what continuous shopping looks like. The point of the
    /// figures is not to make abuse impossible; it is to make it slow enough that the row caps have
    /// time to be the thing that stops it.
    /// </para>
    /// </summary>
    public static DemoSafetySettings Defaults { get; } = new()
    {
        WriteBurstPerSession = 40,
        WriteTokensPerPeriodPerSession = 20,
        WriteBurstPerAddress = 200,
        WriteTokensPerPeriodPerAddress = 60,
        ReadBurstPerAddress = 300,
        ReadTokensPerPeriodPerAddress = 120,
        Quotas = DemoQuotaOptions.Defaults,
    };

    /// <summary>
    /// Reads the section, falling back per key. Hand-bound and incapable of throwing, for the
    /// reason <see cref="DemoQuotaOptions.Read"/> gives: this host is composed by the build-time
    /// OpenAPI generator as well as by a deployment.
    /// </summary>
    public static DemoSafetySettings Read(IConfiguration? configuration, ILogger? logger) => new()
    {
        WriteBurstPerSession = ReadPositive(
            configuration, logger, nameof(WriteBurstPerSession), Defaults.WriteBurstPerSession),
        WriteTokensPerPeriodPerSession = ReadPositive(
            configuration, logger, nameof(WriteTokensPerPeriodPerSession), Defaults.WriteTokensPerPeriodPerSession),
        WriteBurstPerAddress = ReadPositive(
            configuration, logger, nameof(WriteBurstPerAddress), Defaults.WriteBurstPerAddress),
        WriteTokensPerPeriodPerAddress = ReadPositive(
            configuration, logger, nameof(WriteTokensPerPeriodPerAddress), Defaults.WriteTokensPerPeriodPerAddress),
        ReadBurstPerAddress = ReadPositive(
            configuration, logger, nameof(ReadBurstPerAddress), Defaults.ReadBurstPerAddress),
        ReadTokensPerPeriodPerAddress = ReadPositive(
            configuration, logger, nameof(ReadTokensPerPeriodPerAddress), Defaults.ReadTokensPerPeriodPerAddress),
        Quotas = DemoQuotaOptions.Read(configuration, logger),
    };

    private static int ReadPositive(IConfiguration? configuration, ILogger? logger, string key, int fallback)
    {
        var configured = configuration?[$"{SectionName}:{key}"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        logger?.LogWarning(
            "{Key} is '{Value}', which is not a positive whole number. Falling back to {Fallback}.",
            $"{SectionName}:{key}",
            configured,
            fallback);

        return fallback;
    }
}
