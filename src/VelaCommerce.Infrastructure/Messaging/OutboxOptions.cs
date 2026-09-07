using System.Globalization;

using Microsoft.Extensions.Configuration;

namespace VelaCommerce.Infrastructure.Messaging;

/// <summary>
/// How the outbox dispatcher behaves, with a working default for every value so a fresh clone
/// settles a payment with no configuration at all.
/// <para>
/// Bound by hand rather than through <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>,
/// matching <c>PaymentSimulatorOptions</c>: Infrastructure does not take a package reference for a
/// single call, and the tradeoff — a malformed value throws at registration with the key name in
/// the message, instead of binding silently to default — is the one worth having for values that
/// decide whether a payment is ever confirmed.
/// </para>
/// </summary>
public sealed record OutboxOptions
{
    /// <summary>Configuration section. Colon-separated: <c>Messaging:Outbox</c>.</summary>
    public const string SectionName = "Messaging:Outbox";

    /// <summary>
    /// Where settlement notifications are posted when no absolute <c>ReceiverUrl</c> is configured.
    /// <para>
    /// The path only. The origin is discovered from the addresses the host is already listening on
    /// (see <see cref="ResolveReceiverUrl"/>), because a port written down here would be wrong the
    /// first time anybody changed <c>launchSettings.json</c> or deployed into a container that
    /// listens on 8080.
    /// </para>
    /// </summary>
    public const string DefaultReceiverPath = "/api/payments/webhook";

    /// <summary>
    /// Whether the dispatcher runs. Off is a legitimate configuration — a second replica that
    /// should not deliver, or a test that drives <c>SweepAsync</c> by hand — and it does not
    /// change what checkout writes: the messages still accumulate and are still delivered by
    /// whoever is dispatching.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The absolute URL to POST to, if configuration names one. Left null to be discovered from
    /// the host's own listening addresses at startup.
    /// </summary>
    public Uri? ReceiverUrl { get; init; }

    /// <summary>Path appended to a discovered origin when <see cref="ReceiverUrl"/> is not configured.</summary>
    public string ReceiverPath { get; init; } = DefaultReceiverPath;

    /// <summary>
    /// How often to look for due messages.
    /// <para>
    /// One second, because the thing a reviewer is watching is an order flipping to Paid a few
    /// seconds after checkout, and a slower poll shows up directly as a slower demo. The honest
    /// cost is that a polling loop keeps the database awake — which matters on a serverless
    /// Postgres billed by compute-hour, and is why production would replace the poll with an
    /// in-process signal from the enqueue, a queue, or <c>LISTEN</c>/<c>NOTIFY</c> — rather than by
    /// turning this number up. <b>The <c>LISTEN</c>/<c>NOTIFY</c> option is not available on the
    /// connection this app actually uses:</b> Neon's pooled endpoint is PgBouncer in transaction
    /// mode, which breaks it along with SET/RESET, SQL-level PREPARE, temp tables and session
    /// advisory locks (docs/PLAN.md §10). It would need a dedicated connection on the direct
    /// endpoint. This sentence used to recommend it flatly, which would have sent somebody at a
    /// failure that reads like a Neon outage. Note that the reservation reaper already sweeps every
    /// minute, so this process was never going to let the database idle anyway.
    /// </para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most messages one sweep will deliver. Bounded so a backlog is worked off across several
    /// sweeps instead of in one loop that holds a scope, a connection and a socket for as long as
    /// the backlog is long.
    /// </summary>
    public int BatchSize { get; init; } = 10;

    /// <summary>
    /// Total delivery attempts before a message is abandoned. Five, with the backoff below, is
    /// roughly half a minute of trying — long enough to ride out a receiver that is still starting
    /// up, short enough that a genuinely undeliverable message stops burning sweeps quickly.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// The first retry delay. Doubles per failure up to <see cref="MaxRetryDelay"/>.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The ceiling on the doubling, so a long-lived message cannot schedule itself into next week.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long one POST may take before it counts as a failure.
    /// <para>
    /// This is not only politeness to the receiver. The dispatcher holds its claim on a message —
    /// a row lock — for the length of the request, so this value is also the bound on how long one
    /// stalled receiver can keep a row invisible to another replica.
    /// </para>
    /// </summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads the section, falling back to the default for anything absent, and resolves the
    /// receiver URL from the host's own configuration.
    /// </summary>
    /// <param name="configuration">
    /// Root configuration, not the section: resolving the receiver URL needs the host's
    /// <c>urls</c> / <c>ASPNETCORE_URLS</c> / <c>HTTP_PORTS</c> keys, which live above the section.
    /// </param>
    public static OutboxOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = new OutboxOptions();

        var receiverPath = Read(section, nameof(ReceiverPath)) ?? defaults.ReceiverPath;

        return new OutboxOptions
        {
            Enabled = ReadBoolean(section, nameof(Enabled), defaults.Enabled),
            ReceiverUrl = ResolveReceiverUrl(configuration, Read(section, nameof(ReceiverUrl)), receiverPath),
            ReceiverPath = receiverPath,
            PollInterval = ReadTimeSpan(section, nameof(PollInterval), defaults.PollInterval),
            BatchSize = ReadInt32(section, nameof(BatchSize), defaults.BatchSize),
            MaxAttempts = ReadInt32(section, nameof(MaxAttempts), defaults.MaxAttempts),
            RetryBaseDelay = ReadTimeSpan(section, nameof(RetryBaseDelay), defaults.RetryBaseDelay),
            MaxRetryDelay = ReadTimeSpan(section, nameof(MaxRetryDelay), defaults.MaxRetryDelay),
            DeliveryTimeout = ReadTimeSpan(section, nameof(DeliveryTimeout), defaults.DeliveryTimeout),
        };
    }

    /// <summary>
    /// Works out where to post, without anybody writing a port down.
    /// <para>
    /// The receiver is this same application, so the address the host is already listening on is
    /// the correct answer and the only one that stays correct: <c>launchSettings.json</c> picks
    /// 5008 today, a container image sets <c>ASPNETCORE_HTTP_PORTS=8080</c>, and a reverse proxy
    /// changes neither. Hard-coding a default port would work on exactly one of those and fail
    /// silently on the rest, because a failed POST looks like a receiver that is down.
    /// </para>
    /// <para>
    /// HTTP is preferred over HTTPS when the host offers both. A loopback call to the ASP.NET Core
    /// development certificate fails validation on a machine that has not trusted it, which would
    /// present as an unsignable-looking delivery failure with nothing to do with signatures.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> when nothing can be discovered — during build-time OpenAPI
    /// generation, or in a test host with no listener — and the dispatcher then declines to start
    /// rather than posting into the void. Messages stay <c>Pending</c> and undelivered, which is
    /// recoverable; abandoning them after five failed attempts against a port nobody is listening
    /// on would not be.
    /// </para>
    /// </summary>
    public static Uri? ResolveReceiverUrl(IConfiguration configuration, string? configuredUrl, string receiverPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var configured)
                ? configured
                : throw new InvalidOperationException(
                    $"{SectionName}:{nameof(ReceiverUrl)} is '{configuredUrl}', which is not an absolute URL. "
                    + "Give the complete endpoint, e.g. 'https://vela.example.com/api/payments/webhook', or "
                    + $"remove the key and set {SectionName}:{nameof(ReceiverPath)} to have the origin discovered "
                    + "from the addresses this host is listening on.");
        }

        var origin = DiscoverOrigin(configuration);

        return origin is null ? null : new Uri(origin, receiverPath);
    }

    /// <summary>
    /// The delay before attempt number <c>failures + 1</c>, given how many attempts have already
    /// failed. Exponential, capped.
    /// <para>
    /// No jitter, and that is a decision rather than an omission: jitter exists to stop many
    /// clients retrying in lockstep against one server, and here every message is retried by a
    /// single loop against a receiver inside the same process. Adding randomness would only make
    /// the schedule untestable.
    /// </para>
    /// </summary>
    public TimeSpan RetryDelayAfter(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failures);

        // Shift capped at 20 before it is applied, so a message with an absurd attempt count
        // cannot overflow the multiplication on its way to a value that would be clamped anyway.
        var doublings = Math.Min(failures, 20);
        var ticks = RetryBaseDelay.Ticks * (1L << doublings);

        return ticks >= MaxRetryDelay.Ticks || ticks < 0
            ? MaxRetryDelay
            : TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Refuses a configuration that cannot work. Called at registration, so a bad value stops the
    /// host rather than showing up as settlements that never arrive.
    /// </summary>
    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(PollInterval)} must be positive; a zero interval is a spin loop, not a poll.");

        if (BatchSize < 1)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BatchSize)} is {BatchSize}. A sweep that may deliver nothing is a "
                + "dispatcher that never delivers anything.");

        if (MaxAttempts < 1)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxAttempts)} is {MaxAttempts}. Every message would be abandoned before "
                + "it was ever tried.");

        if (RetryBaseDelay <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(RetryBaseDelay)} must be positive; retrying immediately and forever is "
                + "how one unreachable receiver becomes a busy loop.");

        if (MaxRetryDelay < RetryBaseDelay)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxRetryDelay)} ({MaxRetryDelay}) is shorter than "
                + $"{nameof(RetryBaseDelay)} ({RetryBaseDelay}), so the cap would shorten the first retry instead "
                + "of bounding the last one.");

        if (DeliveryTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(DeliveryTimeout)} must be positive. It also bounds how long one message's "
                + "row lock is held, so 'no timeout' means 'one stalled receiver blocks the queue'.");

        if (string.IsNullOrWhiteSpace(ReceiverPath))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ReceiverPath)} must not be empty. Remove the key to take the default "
                + $"'{DefaultReceiverPath}'.");
    }

    /// <summary>
    /// Finds an origin this process is listening on, in the order the host itself would.
    /// <para>
    /// <c>urls</c> is the configuration key ASP.NET Core reads and the one <c>ASPNETCORE_URLS</c>
    /// populates; <c>HTTP_PORTS</c> is the newer form the aspnet container images set. Reading
    /// configuration rather than <c>IServerAddressesFeature</c> keeps Infrastructure free of a
    /// dependency on the web host — and works before the server has bound anything, which is when
    /// this is read.
    /// </para>
    /// </summary>
    private static Uri? DiscoverOrigin(IConfiguration configuration)
    {
        var urls = configuration["urls"]
                   ?? configuration["ASPNETCORE_URLS"]
                   ?? configuration["Kestrel:Endpoints:Http:Url"];

        if (!string.IsNullOrWhiteSpace(urls))
        {
            var candidates = urls
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Loopback)
                .Where(static candidate => candidate is not null)
                .Select(static candidate => candidate!)
                .ToList();

            var chosen = candidates.Find(static candidate => candidate.Scheme == Uri.UriSchemeHttp)
                         ?? candidates.FirstOrDefault();

            if (chosen is not null)
                return chosen;
        }

        var ports = configuration["HTTP_PORTS"] ?? configuration["ASPNETCORE_HTTP_PORTS"];

        if (!string.IsNullOrWhiteSpace(ports))
        {
            var first = ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                return new Uri($"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}");
        }

        return null;
    }

    /// <summary>
    /// Rewrites a listening address into one that can be dialled.
    /// <para>
    /// A host listens on wildcards — <c>http://+:8080</c>, <c>http://*:5000</c>,
    /// <c>http://0.0.0.0:8080</c> — and none of those is a name a client can connect to. The
    /// wildcard means "every interface", so loopback is always one of them and is the right
    /// choice for a call this process makes to itself: it never leaves the machine.
    /// </para>
    /// </summary>
    private static Uri? Loopback(string address)
    {
        var separator = address.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        var scheme = address[..separator];
        var rest = address[(separator + 3)..];

        var portSeparator = rest.LastIndexOf(':');
        var host = portSeparator < 0 ? rest : rest[..portSeparator];
        var port = portSeparator < 0 ? string.Empty : rest[portSeparator..];

        // Trailing path segments on a listening address are legal ("http://localhost:5000/base");
        // they are dropped here because the receiver path is appended separately.
        var slash = port.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
            port = port[..slash];

        if (host is "+" or "*" or "0.0.0.0" or "[::]" or "::")
            host = "localhost";

        return Uri.TryCreate($"{scheme}://{host}{port}", UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string? Read(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan ReadTimeSpan(IConfiguration section, string key, TimeSpan fallback)
    {
        var value = Read(section, key);
        if (value is null) return fallback;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"{SectionName}:{key} is '{value}', which is not a TimeSpan. Use the invariant form, e.g. '00:00:02'.");
    }

    private static int ReadInt32(IConfiguration section, string key, int fallback)
    {
        var value = Read(section, key);
        if (value is null) return fallback;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{SectionName}:{key} is '{value}', which is not a whole number.");
    }

    private static bool ReadBoolean(IConfiguration section, string key, bool fallback)
    {
        var value = Read(section, key);
        if (value is null) return fallback;

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{SectionName}:{key} is '{value}', which is not true or false.");
    }
}
