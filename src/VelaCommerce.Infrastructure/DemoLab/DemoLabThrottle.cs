using System.Collections.Concurrent;

namespace VelaCommerce.Infrastructure.DemoLab;

/// <summary>
/// Decides whether a Demo Lab run may start, and is the reason a reviewer holding down the button
/// cannot take the shop out.
/// <para>
/// <b>Why the shop's own rate limiter is not enough.</b> That limiter sees one POST and charges one
/// token for it. This endpoint answers a single accepted POST by making up to a hundred more
/// requests to itself, so the thing that needs bounding is not the arrival rate of lab requests but
/// the arrival rate of lab <em>runs</em> — a distinction no general-purpose limiter can make,
/// because from outside they look identical. Everything below is about that amplification factor.
/// </para>
/// <para>
/// <b>Three refusals, deliberately distinguishable.</b> A visitor already running one gets "you
/// have a run in progress"; a visitor who just finished gets a cooldown with a Retry-After; a
/// visitor arriving while somebody else is mid-run waits briefly and is then told the lab is busy.
/// They read differently because they mean different things, and a reviewer who is told "too many
/// requests" when the honest answer is "wait four seconds, somebody else is using it" concludes the
/// demo is broken.
/// </para>
/// <para>
/// <b>No queue beyond <see cref="DemoLabOptions.AdmissionWait"/>.</b> The same reasoning the shop's
/// limiter gives: a queued request is a socket held open for work the server has already decided to
/// deprioritise. A short wait absorbs the common case of one run just finishing; past that, a 429
/// with a Retry-After is both faster and more honest than a spinner.
/// </para>
/// </summary>
public sealed class DemoLabThrottle : IDisposable
{
    /// <summary>
    /// How many finished-run timestamps to keep before pruning.
    /// <para>
    /// The cooldown map is keyed by visitor session, and sessions are free — one GET mints one — so
    /// this dictionary is the one piece of unbounded state the lab could accumulate under abuse.
    /// A few thousand entries is nothing in memory and far more than a demo will ever hold at once;
    /// past that, everything older than the cooldown is by definition no longer deciding anything
    /// and is swept.
    /// </para>
    /// </summary>
    private const int PruneThreshold = 4_096;

    private readonly DemoLabOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots;

    /// <summary>When each visitor's last run finished, as unix milliseconds. Prunes itself.</summary>
    private readonly ConcurrentDictionary<Guid, long> _finishedAt = new();

    /// <summary>Visitors with a run in flight right now. Presence is the whole value.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    /// <summary>
    /// Timestamps of recently started runs, newest last. Keyed on nothing, deliberately: the
    /// per-session cooldown is bypassed by minting a new session, which costs a visitor one
    /// request. This is the bound that does not care who is asking.
    /// </summary>
    private readonly Queue<long> _recentStarts = new();

    private readonly Lock _recentStartsGate = new();

    /// <summary>Builds the throttle. One per host: it owns the concurrency slots.</summary>
    /// <param name="options">The bounds. Validated by the caller at registration.</param>
    /// <param name="time">
    /// The clock, injected rather than read: reading <c>DateTimeOffset.UtcNow</c> is banned by an
    /// architecture test, and a cooldown that cannot be moved forward in a test is a cooldown whose
    /// behaviour has to be waited out in real seconds to check.
    /// </param>
    public DemoLabThrottle(DemoLabOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _options = options;
        _time = time;
        _slots = new SemaphoreSlim(options.MaxConcurrentRuns, options.MaxConcurrentRuns);
    }

    /// <summary>
    /// Asks for permission to run, waiting no longer than
    /// <see cref="DemoLabOptions.AdmissionWait"/>.
    /// </summary>
    /// <param name="sessionId">
    /// The calling visitor. Never a client-supplied value — it comes from the sealed session
    /// cookie, so a caller cannot pick a fresh identity to escape their own cooldown without also
    /// discarding their cart, and the shop's per-address limiter is what covers session farming.
    /// </param>
    /// <param name="cancellationToken">The request's own token; a caller who left stops waiting.</param>
    /// <returns>
    /// An admission carrying the lease to dispose when the run ends, or a refusal to send back with
    /// the number of seconds after which it is worth trying again.
    /// </returns>
    public async Task<DemoLabAdmission> EnterAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return DemoLabAdmission.Refused(
                "The Demo Lab is switched off on this deployment. The scenario catalogue still "
                + "describes what each run would do.",
                retryAfterSeconds: 0);
        }

        // Checked before the slot is taken, so a visitor in cooldown cannot occupy the single
        // concurrency slot merely to be refused once they hold it.
        if (RemainingCooldown(sessionId) is { } remaining)
        {
            return DemoLabAdmission.Refused(
                $"One run per visitor every {Seconds(_options.CooldownPerSession)} seconds. Each run "
                + "creates dozens of simultaneous checkouts against a live database, which is worth "
                + "watching once and not worth replaying on a held-down button.",
                retryAfterSeconds: Ceiling(remaining));
        }

        // The global budget, checked before the slot and before the marker. A visitor rotating
        // sessions defeats everything above this line and nothing below it.
        if (!TryTakeGlobalSlot(out var globalRetryAfter))
        {
            return DemoLabAdmission.Refused(
                $"The lab is limited to {_options.MaxRunsPerWindow} runs every "
                + $"{Seconds(_options.GlobalWindow)} seconds across all visitors. Each run puts "
                + "dozens of simultaneous checkouts on a live database, so the ceiling is on the "
                + "deployment rather than on you.",
                retryAfterSeconds: Ceiling(globalRetryAfter));
        }

        // TryAdd is the whole check: whoever adds the key owns the run. Two simultaneous requests
        // from one visitor are exactly the case this exists for, and one of them loses here rather
        // than both proceeding to seed fixtures and race each other's stock.
        if (!_running.TryAdd(sessionId, 0))
        {
            return DemoLabAdmission.Refused(
                "This visitor already has a lab run in progress. Wait for it to finish - its "
                + "transcript is on its way back on the other request.",
                retryAfterSeconds: Ceiling(_options.RunTimeout));
        }

        try
        {
            if (!await _slots.WaitAsync(_options.AdmissionWait, cancellationToken).ConfigureAwait(false))
            {
                _running.TryRemove(sessionId, out _);

                return DemoLabAdmission.Refused(
                    "Another visitor's run is using the lab. Runs are deliberately serialised: two "
                    + "overlapping fifty-way races would put a hundred checkouts on the connection "
                    + "pool at once, which would take the shop down rather than demonstrate it.",
                    retryAfterSeconds: Ceiling(_options.AdmissionWait));
            }
        }
        catch (OperationCanceledException)
        {
            // The caller gave up while waiting. Leaving the marker behind would lock this visitor
            // out of the lab until the process restarted.
            _running.TryRemove(sessionId, out _);
            throw;
        }

        return DemoLabAdmission.Granted(new DemoLabLease(this, sessionId));
    }

    /// <summary>
    /// Releases a run's slot and starts its visitor's cooldown. Called by the lease, once.
    /// </summary>
    /// <summary>
    /// Takes one slot from the rolling global budget, or reports how long until one frees.
    /// Starts are recorded rather than completions: a run that is still going is still load.
    /// </summary>
    private bool TryTakeGlobalSlot(out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var windowTicks = _options.GlobalWindow.Ticks;

        lock (_recentStartsGate)
        {
            while (_recentStarts.Count > 0 && now - _recentStarts.Peek() >= windowTicks)
            {
                _recentStarts.Dequeue();
            }

            if (_recentStarts.Count >= _options.MaxRunsPerWindow)
            {
                retryAfter = TimeSpan.FromTicks(windowTicks - (now - _recentStarts.Peek()));
                return false;
            }

            _recentStarts.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void Exit(Guid sessionId)
    {
        _finishedAt[sessionId] = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _running.TryRemove(sessionId, out _);
        _slots.Release();

        PruneIfCrowded();
    }

    /// <summary>
    /// How much of this visitor's cooldown is left, or <see langword="null"/> if none is.
    /// </summary>
    private TimeSpan? RemainingCooldown(Guid sessionId)
    {
        if (_options.CooldownPerSession <= TimeSpan.Zero)
        {
            return null;
        }

        if (!_finishedAt.TryGetValue(sessionId, out var finishedAt))
        {
            return null;
        }

        var elapsed = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(finishedAt);

        // A negative elapsed means the clock moved backwards - an NTP correction, or a test clock
        // being rewound. Treating that as "still in cooldown" would lock a visitor out for however
        // far time jumped, so it reads as expired.
        return elapsed >= _options.CooldownPerSession || elapsed < TimeSpan.Zero
            ? null
            : _options.CooldownPerSession - elapsed;
    }

    /// <summary>
    /// Drops timestamps that can no longer refuse anybody. Cheap, and only when the map is large.
    /// </summary>
    private void PruneIfCrowded()
    {
        if (_finishedAt.Count <= PruneThreshold)
        {
            return;
        }

        var cutoff = _time.GetUtcNow().Add(-_options.CooldownPerSession).ToUnixTimeMilliseconds();

        foreach (var entry in _finishedAt)
        {
            if (entry.Value < cutoff)
            {
                // TryRemove with the observed value, so a run that finished between the read and
                // the removal keeps its fresh timestamp instead of losing its cooldown.
                _finishedAt.TryRemove(new KeyValuePair<Guid, long>(entry.Key, entry.Value));
            }
        }
    }

    private static int Ceiling(TimeSpan span) => Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));

    private static int Seconds(TimeSpan span) => Math.Max(0, (int)span.TotalSeconds);

    /// <summary>Releases the semaphore's own handles. The host owns this object's lifetime.</summary>
    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// A granted run, and the only way to give the slot back.
    /// <para>
    /// A disposable rather than a matching <c>Exit</c> call, so the release rides on
    /// <c>using</c> and happens on the exception path, the timeout path and the "the reviewer
    /// closed the tab" path without any of them being remembered separately. Releasing twice
    /// would inflate the semaphore's count and quietly raise the concurrency ceiling, so the
    /// first disposal wins and the rest are no-ops.
    /// </para>
    /// </summary>
    public sealed class DemoLabLease : IDisposable
    {
        private readonly DemoLabThrottle _throttle;
        private readonly Guid _sessionId;
        private int _released;

        internal DemoLabLease(DemoLabThrottle throttle, Guid sessionId)
        {
            _throttle = throttle;
            _sessionId = sessionId;
        }

        /// <summary>Gives the slot back and starts the cooldown. Idempotent.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _throttle.Exit(_sessionId);
            }
        }
    }
}

/// <summary>
/// The answer to "may this run start?": a lease, or a refusal a caller can turn into a 429 without
/// inventing any of the wording.
/// </summary>
/// <param name="Lease">The lease to dispose when the run ends, or null when refused.</param>
/// <param name="Refusal">Why not, in a sentence that can be shown to a person.</param>
/// <param name="RetryAfterSeconds">What to put in Retry-After. Zero when retrying will not help.</param>
public sealed record DemoLabAdmission(
    DemoLabThrottle.DemoLabLease? Lease,
    string? Refusal,
    int RetryAfterSeconds)
{
    /// <summary>Whether the run may proceed.</summary>
    public bool Admitted => Lease is not null;

    internal static DemoLabAdmission Granted(DemoLabThrottle.DemoLabLease lease) => new(lease, null, 0);

    internal static DemoLabAdmission Refused(string refusal, int retryAfterSeconds) =>
        new(null, refusal, retryAfterSeconds);
}
