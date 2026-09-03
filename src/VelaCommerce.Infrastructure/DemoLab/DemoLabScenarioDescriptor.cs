namespace VelaCommerce.Infrastructure.DemoLab;

/// <summary>
/// One thing the Demo Lab can prove, described well enough that a reviewer knows what a button
/// will do before pressing it.
/// <para>
/// The fields are split the way they are because a reviewer with ten minutes is asking four
/// separate questions and a single prose blob answers none of them well: <em>what is being
/// claimed</em> (<see cref="Claim"/>), <em>what rule holds it up</em> (<see cref="Invariant"/>),
/// <em>what actually enforces it</em> (<see cref="Mechanism"/> — a statement, an index, a
/// constraint, not a layer), and <em>who else says so</em> (<see cref="ProvenBy"/>, a file they
/// can open). The last one is the important one: the lab is a demonstration, and a demonstration
/// is only evidence if it agrees with something that runs in CI.
/// </para>
/// </summary>
/// <param name="Id">Stable, URL-safe, and part of the run route. Renaming one breaks a permalink.</param>
/// <param name="Title">The button's label.</param>
/// <param name="Claim">The commercial promise, in a shopper's words rather than a developer's.</param>
/// <param name="Invariant">The rule that must hold for the claim to be true.</param>
/// <param name="Mechanism">What enforces it — the statement, index or constraint that decides.</param>
/// <param name="ProvenBy">Repository-relative path of the test file that proves the same thing in CI.</param>
/// <param name="ProvenByTest">The test method, so the reader lands on the right one in a long file.</param>
/// <param name="Participants">
/// How many simultaneous shoppers the run creates. The load a single button press produces, stated
/// up front rather than discovered.
/// </param>
/// <param name="Units">Units of private fixture stock the run seeds for itself.</param>
/// <param name="Creates">What the run writes, so nothing about the blast radius is a surprise.</param>
/// <param name="Fidelity">
/// How faithful the run is to the claim, in the run's own words. Every scenario here is genuine —
/// real HTTP, real transactions, real races — and this field says exactly which part is genuine so
/// that "genuine" is a checkable statement rather than a reassurance.
/// </param>
public sealed record DemoLabScenarioDescriptor(
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
    string Fidelity);
