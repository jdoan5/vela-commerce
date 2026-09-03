namespace VelaCommerce.Api.Contracts;

/// <summary>
/// One scenario, run end to end against the live shop, with everything needed to disbelieve it.
/// <para>
/// The shape is deliberate and reads top to bottom as an argument: here is the claim, here is the
/// stock I created to test it with, here is every request I sent and every answer I got, here is
/// what the database says afterwards, and here - finally - is whether the invariant held and how I
/// know. A reviewer who trusts none of it can check the last part against the rows in the middle,
/// and check those against the test file named at the top.
/// </para>
/// </summary>
/// <param name="RunId">This run, for correlating a transcript with a log line.</param>
/// <param name="ScenarioId">Which scenario ran.</param>
/// <param name="Title">Its label.</param>
/// <param name="Claim">The commercial promise being tested.</param>
/// <param name="Invariant">The rule the claim rests on.</param>
/// <param name="Mechanism">What enforces it.</param>
/// <param name="ProvenBy">The test file that proves the same thing in CI.</param>
/// <param name="ProvenByTest">The test method inside it.</param>
/// <param name="StartedAt">When the run began.</param>
/// <param name="ElapsedMilliseconds">How long the whole thing took, fixture and teardown included.</param>
/// <param name="Fixture">The private stock this run created to race for.</param>
/// <param name="Steps">The transcript, in order.</param>
/// <param name="Evidence">What the database says, read after the fact.</param>
/// <param name="Verdict">Whether the invariant held, and the checks that decide it.</param>
/// <param name="Caveats">
/// Everything a sceptical reader should know that the steps above do not already say - what was
/// elided, what was not waited for, and anything about this run that differs from the test it
/// mirrors. Empty is a legitimate answer; a missing caveat is not.
/// </param>
public sealed record LabRunResponse(
    string RunId,
    string ScenarioId,
    string Title,
    string Claim,
    string Invariant,
    string Mechanism,
    string ProvenBy,
    string ProvenByTest,
    DateTimeOffset StartedAt,
    long ElapsedMilliseconds,
    LabFixtureResponse Fixture,
    IReadOnlyList<LabStepResponse> Steps,
    LabEvidenceResponse Evidence,
    LabVerdictResponse Verdict,
    IReadOnlyList<string> Caveats);

/// <summary>
/// The stock this run created for itself.
/// <para>
/// Published as part of the result because it is the answer to the obvious objection: a lab that
/// races fifty shoppers for the last unit of something must have got that unit from somewhere, and
/// if the answer were "the shop's own inventory" then pressing the button would empty the shelf for
/// everybody else. It is a private product, live for the length of the run and then destroyed.
/// </para>
/// </summary>
/// <param name="ProductSlug">The fixture product's slug. Unique per run; gone afterwards.</param>
/// <param name="Variants">The variants seeded, with the stock each started with.</param>
/// <param name="Why">Why the lab seeds rather than borrows.</param>
public sealed record LabFixtureResponse(
    string ProductSlug,
    IReadOnlyList<LabFixtureVariantResponse> Variants,
    string Why);

/// <summary>One seeded fixture variant.</summary>
/// <param name="VariantId">The buyable id the run's carts pointed at.</param>
/// <param name="Sku">The SKU a shortfall names.</param>
/// <param name="DisplayName">What it was called.</param>
/// <param name="UnitPrice">
/// The price. Chosen to end in whole dollars so its trailing cents cannot accidentally select a
/// payment scenario - the simulator reads <c>.01</c> to <c>.05</c> as Decline through Reorder.
/// </param>
/// <param name="OnHand">Units on the shelf when the run started.</param>
public sealed record LabFixtureVariantResponse(
    Guid VariantId,
    string Sku,
    string DisplayName,
    MoneyDto UnitPrice,
    int OnHand);

/// <summary>
/// Whether the invariant held, and the individual comparisons that decide it.
/// <para>
/// Every check states what was expected and what was actually found, because a verdict that only
/// said "passed" would be asking to be trusted about precisely the thing in question. A failing
/// check is reported rather than hidden: a lab that could only produce good news would be
/// worthless as evidence, since it would produce it against a broken shop too.
/// </para>
/// </summary>
/// <param name="Held">Whether every check passed.</param>
/// <param name="Invariant">The rule, restated so the verdict stands on its own.</param>
/// <param name="HowWeKnow">The reasoning in a sentence: which evidence settles it, and why.</param>
/// <param name="Checks">The comparisons, each independently readable.</param>
public sealed record LabVerdictResponse(
    bool Held,
    string Invariant,
    string HowWeKnow,
    IReadOnlyList<LabCheckResponse> Checks);

/// <summary>One comparison between what was claimed and what was found.</summary>
/// <param name="Claim">What this check is about.</param>
/// <param name="Expected">What the invariant requires.</param>
/// <param name="Actual">What the run and the database actually produced.</param>
/// <param name="Passed">Whether they agree.</param>
public sealed record LabCheckResponse(
    string Claim,
    string Expected,
    string Actual,
    bool Passed);
