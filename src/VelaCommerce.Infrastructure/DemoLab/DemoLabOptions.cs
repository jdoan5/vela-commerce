using System.Globalization;

using Microsoft.Extensions.Configuration;

namespace VelaCommerce.Infrastructure.DemoLab;

/// <summary>
/// The bounds on a Demo Lab run.
/// <para>
/// Every number here exists because the endpoint that reads them is <em>public and
/// unauthenticated</em>, and because the headline scenario it runs is genuinely fifty simultaneous
/// checkouts. That is real load — fifty Kestrel requests, fifty database connections, a hundred
/// transactions — deliberately produced by one HTTP call from a stranger. Left unbounded it is an
/// amplification primitive: one request in, a hundred out, repeatable as fast as a held-down
/// button. The controls below are what make it a demonstration instead.
/// </para>
/// <para>
/// <b>Three separate ceilings, because they fail differently.</b>
/// <see cref="MaxParticipants"/> bounds the size of one run. <see cref="MaxConcurrentRuns"/> bounds
/// how many runs exist at once across every visitor, which is the one that protects the connection
/// pool — fifty concurrent checkouts is comfortable, five hundred is not.
/// <see cref="CooldownPerSession"/> bounds the <em>rate</em>, which is what a held-down button
/// actually produces. Any one of them alone leaves an obvious hole: a per-run cap does not stop
/// repetition, a rate limit does not stop two visitors colliding, and a concurrency cap without a
/// cooldown just makes the flood queue politely.
/// </para>
/// <para>
/// These sit <em>underneath</em> the controls the shop already has. Every request the lab makes to
/// itself passes through the same per-session token bucket and the same row quotas as a shopper's,
/// because they are ordinary HTTP requests to ordinary endpoints. Nothing here replaces those; it
/// bounds the one thing they cannot see, which is that a single accepted request is about to
/// become a hundred.
/// </para>
/// </summary>
public sealed record DemoLabOptions
{
    /// <summary>Configuration section. Colon-separated: <c>Demo:Lab</c>.</summary>
    public const string SectionName = "Demo:Lab";

    /// <summary>
    /// Whether the lab may run at all.
    /// <para>
    /// A kill switch that needs no deploy. If the shop is ever struggling, this turns the
    /// amplifier off while leaving the catalogue endpoint answering, so the page can still explain
    /// what the buttons would have done instead of 404-ing and looking broken.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The most simultaneous shoppers one run may create.
    /// <para>
    /// Fifty, because the claim under test is "fifty shoppers racing for five units sell exactly
    /// five" and a lab that quietly ran eight would be demonstrating a smaller statement than the
    /// one printed on the button. Fifty concurrent checkouts hold fifty pooled connections for
    /// roughly a second; Npgsql's default pool is a hundred, and <see cref="MaxConcurrentRuns"/>
    /// keeps that from being multiplied by a second run.
    /// </para>
    /// </summary>
    public int MaxParticipants { get; init; } = 50;

    /// <summary>
    /// How many runs may be in flight at once, across every visitor.
    /// <para>
    /// One. Not a throughput decision — a blast-radius one. Two overlapping fifty-way runs would
    /// put a hundred checkouts on the pool at the same instant, and the failure that produces is
    /// not a slow lab, it is a shop that cannot serve anybody because there are no connections
    /// left. Serialising runs costs a waiting reviewer a second and costs a shopper nothing.
    /// </para>
    /// </summary>
    public int MaxConcurrentRuns { get; init; } = 1;

    /// <summary>
    /// How long a run will wait for its turn before answering 429 rather than queueing.
    /// <para>
    /// Short, and for the reason the shop's own limiter gives for having no queue: a request the
    /// server has already decided to deprioritise should say so, not hold a socket open while a
    /// reviewer watches a spinner. Long enough to absorb one run that is already finishing.
    /// </para>
    /// </summary>
    public TimeSpan AdmissionWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long one visitor must wait between runs.
    /// <para>
    /// This is the control that answers "a reviewer holding down the button". Without it, one
    /// session can re-enter the moment the previous run releases the slot, and the shop spends its
    /// entire capacity replaying the lab. Ten seconds is long enough to make a held button
    /// pointless and short enough that trying three scenarios in a row feels immediate.
    /// </para>
    /// </summary>
    public TimeSpan CooldownPerSession { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most runs this deployment will start in <see cref="GlobalWindow"/>, across every
    /// visitor.
    /// <para>
    /// <see cref="CooldownPerSession"/> bounds one visitor and nothing else, and a demo session is
    /// free — anybody can mint a new one with a request. A reviewer proved the point by rotating
    /// sessions and getting 376 runs through in ten seconds, about 1,850 checkouts a second, past a
    /// cooldown that was working exactly as written. This budget is keyed on nothing, so there is
    /// nothing to rotate.
    /// </para>
    /// </summary>
    public int MaxRunsPerWindow { get; init; } = 8;

    /// <summary>The rolling window <see cref="MaxRunsPerWindow"/> is counted over.</summary>
    public TimeSpan GlobalWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wall-clock budget for one run, after which it is abandoned and reported as
    /// inconclusive.
    /// <para>
    /// A run holds the single concurrency slot, so an unbounded one is an outage: the lab stops
    /// answering for everybody and there is nothing to point at. The budget is deliberately far
    /// above the sub-second these scenarios take, because the thing it is really catching is a
    /// database that has gone away — and a run that trips it still tears down its fixture and
    /// still returns its transcript, with the verdict saying plainly that it did not finish.
    /// </para>
    /// </summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The ceiling on any single request the lab makes to itself.
    /// <para>
    /// Separate from <see cref="RunTimeout"/> so that one stuck call cannot eat the whole budget
    /// and leave the transcript with nothing but a timeout at the end. A request that trips this
    /// is recorded as a failed exchange and the run continues, which is what turns "the shop
    /// stopped answering" into a visible line in the transcript rather than an exception.
    /// </para>
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How much of any one body the transcript will carry, in characters.
    /// <para>
    /// The transcript is rendered by a page, so it has to stay a document rather than a dump. Four
    /// thousand characters holds a complete order or problem response with room to spare; anything
    /// longer is truncated with a marker that says so, because a silently shortened body would be
    /// the one thing in this response a reviewer could not trust.
    /// </para>
    /// </summary>
    public int MaxBodyCharacters { get; init; } = 4_000;

    /// <summary>
    /// Reads the section, falling back to the default for anything absent or unreadable.
    /// </summary>
    /// <param name="configuration">
    /// Root configuration; the <c>Demo:Lab</c> section is read from it. Every key is optional, so
    /// a host with no configuration at all gets the defaults above rather than an exception.
    /// </param>
    public static DemoLabOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = new DemoLabOptions();

        return new DemoLabOptions
        {
            Enabled = ReadBoolean(section, nameof(Enabled), defaults.Enabled),
            MaxParticipants = ReadInt32(section, nameof(MaxParticipants), defaults.MaxParticipants),
            MaxConcurrentRuns = ReadInt32(section, nameof(MaxConcurrentRuns), defaults.MaxConcurrentRuns),
            AdmissionWait = ReadTimeSpan(section, nameof(AdmissionWait), defaults.AdmissionWait),
            CooldownPerSession = ReadTimeSpan(section, nameof(CooldownPerSession), defaults.CooldownPerSession),
            RunTimeout = ReadTimeSpan(section, nameof(RunTimeout), defaults.RunTimeout),
            RequestTimeout = ReadTimeSpan(section, nameof(RequestTimeout), defaults.RequestTimeout),
            MaxBodyCharacters = ReadInt32(section, nameof(MaxBodyCharacters), defaults.MaxBodyCharacters),
        };
    }

    /// <summary>
    /// Refuses a configuration that would remove a bound rather than change it.
    /// <para>
    /// Called at registration, where the alternative is a host that starts happily with
    /// <c>MaxConcurrentRuns: 0</c> and a lab that deadlocks on its own semaphore, or with a
    /// negative cooldown and no rate limit at all. These are plain value checks that hold in every
    /// environment, so — unlike the payment simulator's environment-dependent secret check — they
    /// cannot break the build-time OpenAPI generator, which composes this host as Production.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A bound is missing or nonsensical.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParticipants, 2, nameof(MaxParticipants));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxParticipants, 200, nameof(MaxParticipants));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentRuns, 1, nameof(MaxConcurrentRuns));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrentRuns, 8, nameof(MaxConcurrentRuns));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBodyCharacters, 256, nameof(MaxBodyCharacters));

        if (AdmissionWait < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(AdmissionWait)} cannot be negative.");
        }

        if (CooldownPerSession < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CooldownPerSession)} cannot be negative. Setting it to zero "
                + "removes the per-visitor rate limit, which is a decision; a negative value is a typo.");
        }

        if (RunTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(RunTimeout)} must be positive. A run holds the single "
                + "concurrency slot, so a run with no budget is an outage waiting for a slow query.");
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > RunTimeout)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(RequestTimeout)} must be positive and no larger than "
                + $"{nameof(RunTimeout)}; one stalled request must not be able to consume the whole run.");
        }
    }

    private static string? Read(IConfigurationSection section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBoolean(IConfigurationSection section, string key, bool fallback) =>
        bool.TryParse(Read(section, key), out var parsed) ? parsed : fallback;

    private static int ReadInt32(IConfigurationSection section, string key, int fallback) =>
        int.TryParse(Read(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static TimeSpan ReadTimeSpan(IConfigurationSection section, string key, TimeSpan fallback) =>
        TimeSpan.TryParse(Read(section, key), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
