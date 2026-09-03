namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// One row of the scenario table: how to trigger a path, and what it is there to prove.
/// <para>
/// Prose lives in data rather than in a markdown file so the table cannot describe behaviour the
/// simulator no longer has. Adding a scenario to the enum without adding a descriptor here leaves
/// a visible hole in the published table, which is a better failure than a silently undocumented
/// path.
/// </para>
/// </summary>
/// <param name="Scenario">The scenario, whose name is also the accepted hint value.</param>
/// <param name="AmountTrigger">The amount that selects it when no hint is given.</param>
/// <param name="AuthorizationResult">What <c>AuthorizeAsync</c> returns.</param>
/// <param name="Webhooks">What is queued for delivery afterwards.</param>
/// <param name="Demonstrates">The reason this path is worth a reviewer's attention.</param>
public sealed record PaymentScenarioDescriptor(
    PaymentSimulatorScenario Scenario,
    string AmountTrigger,
    string AuthorizationResult,
    string Webhooks,
    string Demonstrates);
