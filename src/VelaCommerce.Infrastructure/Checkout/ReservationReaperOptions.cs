using Microsoft.Extensions.Configuration;

namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// Whether the reservation reaper sweeps on its own timer.
/// <para>
/// This exists because it was the only background worker in the solution without it, and the gap
/// was not harmless. <c>OutboxOptions</c> and <c>OrderTimelineOptions</c> both carry an
/// <c>Enabled</c> flag, and the integration hosts set both to <c>false</c> so that — in
/// <c>SettlementHost</c>'s own words — every sweep in the suite is one a test asked for. The reaper
/// had no such switch, so all three test hosts ran a live one against the shared container on the
/// system clock, sweeping on boot and every minute after. Any test that backdated a reservation to
/// make it lapse was racing an uncontrolled third writer on a sixty-second fuse, and the comment
/// promising otherwise was describing something that was not true of this worker.
/// </para>
/// <para>
/// Bound by hand rather than through <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>,
/// matching the other three options records in this assembly: Infrastructure does not take a
/// package reference for a single call, and a malformed value throwing at registration with the key
/// name in the message beats binding silently to a default.
/// </para>
/// </summary>
public sealed record ReservationReaperOptions
{
    /// <summary>Configuration section. Colon-separated: <c>Checkout:Reaper</c>.</summary>
    public const string SectionName = "Checkout:Reaper";

    /// <summary>
    /// Whether the worker sweeps on its timer. Off is a legitimate configuration, and the same
    /// three cases the timeline names apply: a second replica that should not double-sweep, an
    /// integration test that wants to drive <c>SweepAsync</c> itself, or a demo being narrated.
    /// <para>
    /// Turning it off changes no data. It leaves lapsed reservations held, which is exactly the
    /// condition the reaper exists to end — so off is a choice to make deliberately and briefly,
    /// not a default worth living with.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How many orders one sweep will take. Bounded so a sweep cannot hold a connection open across
    /// thousands of rows, and configurable for the same reason <c>OutboxOptions</c> and
    /// <c>OrderTimelineOptions</c> expose theirs — plus one this worker has of its own: the batch is
    /// what a starving candidate query would fill, so a test needs to be able to make it small
    /// enough to observe that.
    /// </summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Reads the section, falling back to the default for anything absent.</summary>
    public static ReservationReaperOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = new ReservationReaperOptions();

        return new ReservationReaperOptions
        {
            Enabled = ReadBoolean(section, nameof(Enabled), defaults.Enabled),
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
}
