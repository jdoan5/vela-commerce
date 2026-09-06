using Microsoft.Extensions.Configuration;

namespace VelaCommerce.Infrastructure.Tenancy;

/// <summary>
/// How long a stranger's demo data outlives them, and how often anything looks.
/// <para>
/// Every default here is a cost decision as much as a hygiene one, and two of them are shaped by
/// numbers measured elsewhere in this repository rather than chosen for roundness — see
/// <see cref="FirstSweepDelay"/> and <see cref="SweepInterval"/>.
/// </para>
/// <para>
/// Bound by hand rather than through <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>,
/// matching the four other options records in this assembly: Infrastructure does not take a package
/// reference for a single call, and a malformed value throwing at registration with the key name in
/// the message beats binding silently to a default.
/// </para>
/// </summary>
public sealed record DemoDataPurgeOptions
{
    /// <summary>Configuration section. Colon-separated: <c>Demo:Purge</c>.</summary>
    public const string SectionName = "Demo:Purge";

    /// <summary>
    /// Whether the purge sweeps on its own timer.
    /// <para>
    /// Off is what every integration host sets, for the reason <c>ReservationReaperOptions</c>
    /// spells out at length: a global sweeper running on the system clock against a shared
    /// container turns every other test in the assembly into a race with an uncontrolled third
    /// writer. Off means "nothing sweeps on a timer", not "sweeping is forbidden" — a test still
    /// calls <c>SweepAsync</c> directly.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long demo data survives before it is deleted. 24 hours, which is the figure
    /// <c>docs/PLAN.md</c> specified and the one the tenancy design was argued around.
    /// <para>
    /// <b>This is shorter than the session cookie, and that is a real consequence rather than an
    /// oversight.</b> <c>DemoSessionMiddleware</c> issues a cookie with a 14-day lifetime, so a
    /// visitor who returns on day two still has an identity and will find an empty cart behind it.
    /// The alternative — matching the cookie — keeps fourteen days of abandoned checkouts holding
    /// units on a stock ledger every visitor shares, on a database with a half-gigabyte cap. A
    /// stranger losing a demo cart overnight is the cheaper of the two failures, and it is the one
    /// a reviewer would expect of a demo.
    /// </para>
    /// <para>
    /// Note what the age is measured from for a cart, because there is no choice about it: the
    /// cart's own id. Carts carry no <c>created_at</c> and no <c>updated_at</c> — see
    /// <c>DemoDataPurge</c> for how the id supplies one — so a cart minted 25 hours ago and
    /// added to a minute ago is old. Orders and price overrides both carry real timestamps and do
    /// not share the limitation.
    /// </para>
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long after the host starts before the first sweep runs. Sixty seconds, and the number
    /// comes from a measurement.
    /// <para>
    /// This process scales to zero, so "startup" is a thing a visitor is standing in the middle of:
    /// <c>docs/measurements/cold-start.md</c> puts the cold start at 32 s p50 and 37 s p95 against
    /// 0.16 s warm. A sweep that started at boot would spend a chunk of a quarter of a vCPU on
    /// deleting rows while somebody waits for their first page. Sixty seconds clears the measured
    /// p95 with margin and is comfortably inside the container's 300-second idle cooldown, so the
    /// sweep still happens on every wake that carries real traffic.
    /// </para>
    /// </summary>
    public TimeSpan FirstSweepDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long between sweeps once the first has run. Six hours, which in production means "once,
    /// per wake" and is deliberately not tuned as if it meant anything else.
    /// <para>
    /// The container's cooldown is 300 seconds, so a replica that stops receiving requests is gone
    /// long before this elapses; the interval only ever fires on a host somebody is holding awake.
    /// It is not a cron and does not pretend to be one — <c>docs/adr/0010</c> is the argument for
    /// why this system reaps on visits instead of on a clock.
    /// </para>
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How many orders one sweep will take. Bounded for the same reason the reaper's is: a sweep
    /// must not hold a connection open across thousands of rows. Carts, price overrides and
    /// settled outbox messages are not batched — they delete in one statement each, because none of
    /// them takes a row lock or touches the shared ledger.
    /// </summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Reads the section, falling back to the default for anything absent.</summary>
    public static DemoDataPurgeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = new DemoDataPurgeOptions();

        return new DemoDataPurgeOptions
        {
            Enabled = ReadBoolean(section, nameof(Enabled), defaults.Enabled),
            Retention = ReadTimeSpan(section, nameof(Retention), defaults.Retention),
            FirstSweepDelay = ReadTimeSpan(section, nameof(FirstSweepDelay), defaults.FirstSweepDelay),
            SweepInterval = ReadTimeSpan(section, nameof(SweepInterval), defaults.SweepInterval),
            BatchSize = ReadInt32(section, nameof(BatchSize), defaults.BatchSize)
        };
    }

    private static bool ReadBoolean(IConfiguration section, string key, bool fallback)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{SectionName}:{key} is '{value}', which is not true or false.");
    }

    private static int ReadInt32(IConfiguration section, string key, int fallback)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        if (!int.TryParse(value, out var parsed))
            throw new InvalidOperationException($"{SectionName}:{key} is '{value}', which is not a whole number.");

        return parsed > 0
            ? parsed
            : throw new InvalidOperationException($"{SectionName}:{key} is {parsed}; a sweep that takes no orders does nothing.");
    }

    /// <summary>
    /// Reads a <c>d.hh:mm:ss</c> duration. Zero is rejected everywhere it is read: a zero retention
    /// deletes everything the moment it is written, and a zero interval spins.
    /// </summary>
    private static TimeSpan ReadTimeSpan(IConfiguration section, string key, TimeSpan fallback)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        if (!TimeSpan.TryParse(value, out var parsed))
            throw new InvalidOperationException(
                $"{SectionName}:{key} is '{value}', which is not a duration. Use d.hh:mm:ss, for example 1.00:00:00.");

        return parsed > TimeSpan.Zero
            ? parsed
            : throw new InvalidOperationException(
                $"{SectionName}:{key} is {parsed}. A zero or negative duration here would either delete live data or spin.");
    }
}
