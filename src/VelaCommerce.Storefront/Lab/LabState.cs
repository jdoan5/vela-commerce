namespace VelaCommerce.Storefront.Lab;

/// <summary>
/// What the Demo Lab remembers for the length of a tab: the menu it fetched, the transcripts it has
/// produced, and when the endpoint will next accept a run.
/// <para>
/// <strong>Why this outlives the page.</strong> A reviewer runs the fifty-way race, follows the
/// permalink for the settlement replay, then presses Back. Without somewhere to keep them, both
/// transcripts are gone and the catalogue is fetched a third time — which on a cold container costs
/// several seconds of staring at a waking notice for something already in memory. Keeping the runs
/// per scenario rather than one "last run" is what lets the index page show three completed
/// demonstrations at once, which is exactly how somebody reads this page.
/// </para>
/// <para>
/// <strong>The cooldown is remembered here for the same reason.</strong> The endpoint holds each
/// visitor to one run per cooldown; it is the visitor that is limited, not the page, so navigating
/// away and back does not reset it. Storing the instant here lets every Run button show a countdown
/// rather than letting somebody discover the limit by being refused.
/// </para>
/// <para>
/// Deliberately a plain data holder with no change event. The lab page is the only thing that
/// writes to it and it is the only thing that renders it, so an event would be the page notifying
/// itself.
/// </para>
/// </summary>
public sealed class LabState
{
    private readonly Dictionary<string, LabRunResult> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The menu, once fetched. Null until the first successful read.</summary>
    public LabCatalogDocument? Catalog { get; private set; }

    /// <summary>Why the menu could not be read, when it could not. Null when there is nothing wrong.</summary>
    public LabApiException? CatalogFailure { get; private set; }

    /// <summary>True once a fetch has been attempted, however it went. Distinguishes "empty" from "not asked yet".</summary>
    public bool CatalogAttempted { get; private set; }

    /// <summary>The instant the endpoint will next accept a run from this visitor, if it is known.</summary>
    public DateTimeOffset? ReadyAt { get; private set; }

    /// <summary>Remembers a catalogue that arrived, and clears any earlier failure.</summary>
    /// <param name="catalog">The menu as read.</param>
    public void Remember(LabCatalogDocument catalog)
    {
        Catalog = catalog;
        CatalogFailure = null;
        CatalogAttempted = true;
    }

    /// <summary>Remembers that the catalogue could not be read. The previous catalogue, if any, is kept.</summary>
    /// <param name="failure">What went wrong.</param>
    public void RememberFailure(LabApiException failure)
    {
        CatalogFailure = failure;
        CatalogAttempted = true;
    }

    /// <summary>The result of the last run of one scenario, or null if it has not been run in this tab.</summary>
    /// <param name="scenarioId">The scenario's id.</param>
    public LabRunResult? ResultFor(string? scenarioId) =>
        scenarioId is { Length: > 0 } id && _results.TryGetValue(id, out var result) ? result : null;

    /// <summary>Stores the result of a run, replacing anything that scenario produced earlier.</summary>
    /// <param name="scenarioId">The scenario's id.</param>
    /// <param name="result">What came back.</param>
    public void Remember(string scenarioId, LabRunResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentNullException.ThrowIfNull(result);

        _results[scenarioId] = result;
    }

    /// <summary>Forgets one scenario's transcript — used when a new run of it starts.</summary>
    /// <param name="scenarioId">The scenario's id.</param>
    public void Forget(string? scenarioId)
    {
        if (scenarioId is { Length: > 0 } id)
        {
            _results.Remove(id);
        }
    }

    /// <summary>
    /// Starts the cooldown the endpoint would enforce anyway.
    /// <para>
    /// Called after every run, successful or refused: a run that completed spends the visitor's
    /// turn, and a 429 arrives with the endpoint's own <c>Retry-After</c> saying how much of
    /// somebody else's turn is left.
    /// </para>
    /// </summary>
    /// <param name="seconds">Seconds to wait. Zero or less clears the cooldown.</param>
    public void StartCooldown(int seconds) =>
        ReadyAt = seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;

    /// <summary>Whole seconds left on the cooldown, or zero when a run may start now.</summary>
    public int SecondsUntilReady
    {
        get
        {
            if (ReadyAt is not { } ready)
            {
                return 0;
            }

            var remaining = ready - DateTimeOffset.UtcNow;

            return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
        }
    }
}
