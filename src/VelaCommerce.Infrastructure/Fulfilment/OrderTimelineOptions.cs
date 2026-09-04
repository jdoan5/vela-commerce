using System.Globalization;

using Microsoft.Extensions.Configuration;

using VelaCommerce.Infrastructure.Checkout;

namespace VelaCommerce.Infrastructure.Fulfilment;

/// <summary>
/// How fast the demo's order lifecycle runs, with a working default for every value so a fresh
/// clone shows a reviewer the whole timeline without configuring anything.
///
/// <para>
/// <b>Why these are configuration and <c>CheckoutPolicy</c>'s durations are constants.</b> That
/// file argues, correctly, that a value which changes the meaning of a persisted row belongs in a
/// deployment rather than in a config key. These are the opposite kind of value. A dwell time
/// changes nothing about what a row <em>means</em> — an order is Packed when it is Packed — it
/// only changes how long the demo waits before saying so. Twenty seconds is a stage direction for
/// a reviewer who will not wait a week, and the number a maintainer would want on a screen share,
/// in a load test and in a recorded walkthrough is a different number each time. That is exactly
/// the thing a rebuild should not stand between.
/// </para>
///
/// <para>
/// Bound by hand rather than through <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>,
/// matching <c>OutboxOptions</c> and <c>PaymentSimulatorOptions</c>: Infrastructure does not take a
/// package reference for a single call, and a malformed value throwing at registration with the key
/// name in the message beats binding silently to default.
/// </para>
/// </summary>
public sealed record OrderTimelineOptions
{
    /// <summary>Configuration section. Colon-separated: <c>Fulfilment:Timeline</c>.</summary>
    public const string SectionName = "Fulfilment:Timeline";

    /// <summary>
    /// Whether the worker advances anything. Off is a legitimate configuration: a second replica
    /// that should not double-drive the timeline, an integration test that wants orders to sit
    /// still, or a demo being narrated step by step. Turning it off changes no data — orders stay
    /// where they are and advance the moment a worker runs again.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long an order rests in <c>Paid</c> before it is packed.
    /// <para>
    /// Long enough that a reviewer sees "Paid" as a state rather than a flicker between two
    /// others, short enough that they do not go and make coffee. The default puts the whole
    /// Pending → Shipped story inside a minute, which is roughly the length of the attention a
    /// portfolio project gets.
    /// </para>
    /// </summary>
    public TimeSpan PaidDwell { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long an order rests in <c>Packed</c> before it ships.
    /// <para>
    /// Longer than <see cref="PaidDwell"/> on purpose. Shipping is the step that actually moves
    /// stock — on-hand drops, the reservation is honoured — so it is the one worth leaving on
    /// screen long enough to open the inventory page and watch the number change.
    /// </para>
    /// </summary>
    public TimeSpan PackedDwell { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>
    /// How often to look for orders that are due.
    /// <para>
    /// This is the timeline's resolution: an order becomes due at some instant and is advanced at
    /// the next sweep, so this value is the worst-case lateness of every transition. Two seconds
    /// against a twenty-second dwell is a ten-percent error nobody watching can perceive, and it
    /// is deliberately slower than the outbox's one-second poll — the outbox is racing to make a
    /// payment look instant, this is pacing something meant to be watched. The honest cost is the
    /// same one the outbox names: a polling loop keeps the database awake, which matters on a
    /// serverless Postgres billed by compute-hour.
    /// </para>
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The most orders one sweep will advance. Bounded like the reaper's, so a backlog — a
    /// container that was down for an hour, a load test's worth of orders — is worked off across
    /// several sweeps instead of in one transaction-per-order loop that holds a scope and a
    /// connection for as long as the backlog is long.
    /// </summary>
    public int BatchSize { get; init; } = 25;

    /// <summary>
    /// How long after payment an order becomes due for <paramref name="step"/>.
    ///
    /// <para>
    /// <b>This is the single place the schedule is expressed, and it is anchored on
    /// <c>PaidAt</c> for both steps.</b> The alternative — measure the ship dwell from the moment
    /// the row actually flipped to Packed — needs a <c>packed_at</c> column, and this phase has
    /// exactly one migration, owned by the outbox slice. Deriving from <c>PaidAt</c> is not merely
    /// the cheap way out:
    /// </para>
    /// <list type="bullet">
    /// <item>The whole timeline becomes a pure function of one committed timestamp, so the same
    /// arithmetic that decides a transition can be run by a UI to render the schedule ahead of
    /// time, and by a test without a clock.</item>
    /// <item>It is self-correcting. A worker that was asleep for an hour catches an order up to
    /// where it should be rather than restarting its clock, because "due" is measured against the
    /// payment, not against the worker's own last action.</item>
    /// </list>
    /// <para>
    /// The cost is one visible behaviour: after an outage, an order overdue for both steps is
    /// packed on one sweep and shipped on the next rather than a further <see cref="PackedDwell"/>
    /// later. For a catch-up that is the wanted behaviour. If a true <c>packed_at</c> is ever
    /// added, this method is the only thing that changes.
    /// </para>
    /// </summary>
    public TimeSpan ElapsedSincePaidBefore(OrderTimelineStep step) => step switch
    {
        OrderTimelineStep.Pack => PaidDwell,
        OrderTimelineStep.Ship => PaidDwell + PackedDwell,
        _ => throw new ArgumentOutOfRangeException(
            nameof(step),
            step,
            "The worker drives two edges. A third step means the state machine grew one and this "
            + "schedule was not told.")
    };

    /// <summary>
    /// When an order paid at <paramref name="paidAt"/> becomes due for <paramref name="step"/>.
    /// The schedule as a reader thinks of it; <see cref="LatestPaidAtDueBy"/> is the same fact
    /// turned around so a query can use it.
    /// </summary>
    public DateTimeOffset DueAt(OrderTimelineStep step, DateTimeOffset paidAt) =>
        paidAt + ElapsedSincePaidBefore(step);

    /// <summary>
    /// The newest <c>PaidAt</c> that is already due for <paramref name="step"/> at
    /// <paramref name="now"/>.
    /// <para>
    /// The inverse of <see cref="DueAt"/>, and the form the sweep needs: comparing a stored column
    /// against a constant is an index-friendly <c>paid_at &lt;= $1</c>, whereas asking the database
    /// to compute <c>paid_at + interval</c> per row is not.
    /// </para>
    /// </summary>
    public DateTimeOffset LatestPaidAtDueBy(OrderTimelineStep step, DateTimeOffset now) =>
        now - ElapsedSincePaidBefore(step);

    /// <summary>
    /// True when the configured timeline runs past <see cref="CheckoutPolicy.ReservationWindow"/>.
    /// <para>
    /// Not an error, so not part of <see cref="Validate"/> — a maintainer who deliberately slows
    /// the demo to realistic durations is doing something reasonable. It is worth saying out loud
    /// once at startup, though, because of an interaction that is invisible from here: an order
    /// settled by webhook may still be carrying <c>Held</c> reservations, and those reservations
    /// are now invisible to the reaper — it only sweeps orders still Pending — so nothing reclaims
    /// them early. Shipping is what removes them: the worker takes every reservation not already
    /// Released and decrements both counters, so the cost is a longer window in which stock is
    /// promised, not a ledger that disagrees with itself. The reaper used to release them whatever
    /// the order was doing, which was worse — it handed a paid order's stock back to the pool.
    /// Either way the fix is upstream: confirm reservations in the transaction that pays the order.
    /// </para>
    /// </summary>
    public bool OutlastsTheReservationWindow =>
        PaidDwell + PackedDwell >= CheckoutPolicy.ReservationWindow;

    /// <summary>
    /// Reads the section, falling back to the default for anything absent.
    /// </summary>
    /// <param name="configuration">
    /// Root configuration or the section itself — either works, because
    /// <see cref="IConfiguration.GetSection"/> on a section that does not exist returns an empty
    /// one and every value here has a default. Root is what the composition root passes, matching
    /// the other <c>Add*</c> methods.
    /// </param>
    public static OrderTimelineOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = new OrderTimelineOptions();

        return new OrderTimelineOptions
        {
            Enabled = ReadBoolean(section, nameof(Enabled), defaults.Enabled),
            PaidDwell = ReadTimeSpan(section, nameof(PaidDwell), defaults.PaidDwell),
            PackedDwell = ReadTimeSpan(section, nameof(PackedDwell), defaults.PackedDwell),
            SweepInterval = ReadTimeSpan(section, nameof(SweepInterval), defaults.SweepInterval),
            BatchSize = ReadInt32(section, nameof(BatchSize), defaults.BatchSize)
        };
    }

    /// <summary>
    /// Refuses a configuration that cannot work. Called at registration, so a bad value stops the
    /// host rather than showing up as a demo whose orders never move.
    /// <para>
    /// Every check here is a plain value check that is wrong in every environment, which is why it
    /// is safe to run at registration: the build-time OpenAPI generator executes the composition
    /// root as Production, and a validation that depends on the environment breaks the build
    /// rather than a deployment. The payment simulator's secret check learned that the hard way.
    /// </para>
    /// </summary>
    public void Validate()
    {
        if (SweepInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SweepInterval)} must be positive; a zero interval is a spin loop, "
                + "not a poll.");

        if (BatchSize < 1)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BatchSize)} is {BatchSize.ToString(CultureInfo.InvariantCulture)}. A sweep "
                + "that may advance nothing is a timeline that never moves.");

        if (PaidDwell < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(PaidDwell)} is {PaidDwell}. A negative dwell would make every paid order "
                + "due the instant it was paid, which is not 'fast', it is 'no Paid state at all'.");

        if (PackedDwell < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(PackedDwell)} is {PackedDwell}. See {nameof(PaidDwell)}: a negative dwell "
                + "removes the state rather than shortening it.");
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
                $"{SectionName}:{key} is '{value}', which is not a TimeSpan. Use the invariant form, "
                + "e.g. '00:00:20'.");
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
