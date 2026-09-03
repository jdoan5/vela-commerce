namespace VelaCommerce.Api.Contracts;

/// <summary>
/// The Demo Lab's menu: what each button will do, what it will cost the shop, and who else says
/// the claim is true.
/// <para>
/// Written to be read before anything is pressed. A reviewer should be able to fetch this one
/// document, decide which invariant they care about, and know in advance how many simultaneous
/// shoppers a run creates and what rows it writes - rather than discovering both from the
/// transcript afterwards.
/// </para>
/// </summary>
/// <param name="Scenarios">Every runnable scenario, in the order a page should show them.</param>
/// <param name="Limits">The bounds a public, unauthenticated run endpoint is held to.</param>
/// <param name="PaymentScenarios">
/// The simulated gateway's six behaviours, rendered from <c>PaymentScenarioCatalog</c> rather than
/// restated here, so the table on the page and the table in the docs cannot disagree.
/// </param>
/// <param name="StockPolicy">
/// How the lab gets stock to race for, in one sentence, because it is the first thing anybody
/// sensible asks about a public endpoint that sells things.
/// </param>
public sealed record LabScenarioCatalogResponse(
    IReadOnlyList<LabScenarioResponse> Scenarios,
    LabLimitsResponse Limits,
    IReadOnlyList<LabPaymentScenarioResponse> PaymentScenarios,
    string StockPolicy);

/// <summary>One scenario, described well enough to judge before running it.</summary>
/// <param name="Id">Stable id; the last segment of <paramref name="RunPath"/>.</param>
/// <param name="Title">The button's label.</param>
/// <param name="Claim">The commercial promise, in a shopper's words.</param>
/// <param name="Invariant">The rule that has to hold for the claim to be true.</param>
/// <param name="Mechanism">What actually enforces it: a statement, an index, a constraint.</param>
/// <param name="ProvenBy">Repository-relative path of the test file that proves it in CI.</param>
/// <param name="ProvenByTest">The test method inside that file.</param>
/// <param name="Participants">Simultaneous shoppers one run creates - the load, stated up front.</param>
/// <param name="Units">Units of private fixture stock the run seeds for itself.</param>
/// <param name="Creates">Every row the run writes.</param>
/// <param name="Fidelity">Which parts are genuine, so "genuine" is checkable rather than reassuring.</param>
/// <param name="RunPath">The exact path to POST to, so nothing has to be assembled by hand.</param>
public sealed record LabScenarioResponse(
    string Id,
    string Title,
    string Claim,
    string Invariant,
    string Mechanism,
    string ProvenBy,
    string ProvenByTest,
    int Participants,
    int Units,
    string Creates,
    string Fidelity,
    string RunPath);

/// <summary>
/// What the lab will not let a caller do. Published rather than merely enforced: a reviewer
/// evaluating whether this endpoint is safe to expose should not have to read the source to find
/// out, and a rate limit nobody can see reads as a bug the first time it fires.
/// </summary>
/// <param name="Enabled">Whether runs are being accepted at all on this deployment.</param>
/// <param name="MaxParticipants">The ceiling on simultaneous shoppers in one run.</param>
/// <param name="MaxConcurrentRuns">How many runs may exist at once, across every visitor.</param>
/// <param name="CooldownSeconds">How long one visitor waits between runs.</param>
/// <param name="RunTimeoutSeconds">The wall-clock budget after which a run is abandoned.</param>
/// <param name="Policy">Why those numbers, in a sentence.</param>
public sealed record LabLimitsResponse(
    bool Enabled,
    int MaxParticipants,
    int MaxConcurrentRuns,
    int CooldownSeconds,
    int RunTimeoutSeconds,
    string Policy);

/// <summary>One simulated-gateway behaviour, as <c>PaymentScenarioCatalog</c> describes it.</summary>
/// <param name="Scenario">The hint value that selects it.</param>
/// <param name="AmountTrigger">The trailing cents that select it when no hint is given.</param>
/// <param name="AuthorizationResult">What the checkout request itself answers.</param>
/// <param name="Webhooks">What arrives afterwards, and how many times.</param>
/// <param name="Demonstrates">The point of having it.</param>
public sealed record LabPaymentScenarioResponse(
    string Scenario,
    string AmountTrigger,
    string AuthorizationResult,
    string Webhooks,
    string Demonstrates);
