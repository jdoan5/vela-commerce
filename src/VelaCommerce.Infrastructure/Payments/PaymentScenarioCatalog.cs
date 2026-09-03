using System.Globalization;
using System.Text;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Payments;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// How an authorization request becomes a scenario, and the human-readable table of that mapping.
/// <para>
/// Selection is a pure function of the request — never a random draw, never a counter. A reviewer
/// who clicks "trigger a decline" must get a decline every time, a Playwright run must record the
/// same GIF twice, and a failing integration test must fail again on the next run. A simulator
/// that rolls dice is a simulator whose failures cannot be reproduced, which is the one thing it
/// exists to make possible.
/// </para>
/// <para>
/// The table below is the contract a reviewer reads. It is generated from
/// <see cref="Descriptors"/> by <see cref="ToMarkdownTable"/>, so the committed markdown beside
/// this file and anything the storefront renders come from the same source and cannot drift apart.
/// </para>
/// </summary>
public static class PaymentScenarioCatalog
{
    /// <summary>
    /// Trailing minor units that select a scenario when no explicit hint is supplied.
    /// <para>
    /// Only the last two digits are consulted, so <c>$1.01</c>, <c>$47.01</c> and <c>$1,203.01</c>
    /// all decline. Deliberately a small, contiguous block starting at 01: a reviewer who has read
    /// one row of the table can guess the rest, which is worth more here than a scattered set of
    /// values that would collide less often.
    /// </para>
    /// </summary>
    private static readonly Dictionary<long, PaymentSimulatorScenario> MagicCents = new()
    {
        [1] = PaymentSimulatorScenario.Decline,
        [2] = PaymentSimulatorScenario.Abandon,
        [3] = PaymentSimulatorScenario.Duplicate,
        [4] = PaymentSimulatorScenario.Delay,
        [5] = PaymentSimulatorScenario.Reorder
    };

    /// <summary>
    /// Every scenario, in the order the table should read: the three synchronous answers first,
    /// then the three ways an asynchronous settlement can misbehave.
    /// </summary>
    public static IReadOnlyList<PaymentScenarioDescriptor> Descriptors { get; } =
    [
        new(
            PaymentSimulatorScenario.Succeed,
            MagicCentsFor(PaymentSimulatorScenario.Succeed),
            "Succeeded — full amount captured in the response",
            "none",
            "The happy path. The order is marked paid inside the checkout request."),
        new(
            PaymentSimulatorScenario.Decline,
            MagicCentsFor(PaymentSimulatorScenario.Decline),
            "Declined — reason `DoNotHonor`",
            "none",
            "A refused card is a business answer, not an exception. The reservation is released and the cart survives."),
        new(
            PaymentSimulatorScenario.Abandon,
            MagicCentsFor(PaymentSimulatorScenario.Abandon),
            "Abandoned — nothing taken",
            "none",
            "Nobody said no, so nothing is retried. The reservation is left to lapse on its TTL."),
        new(
            PaymentSimulatorScenario.Duplicate,
            MagicCentsFor(PaymentSimulatorScenario.Duplicate),
            "PendingSettlement",
            "2 x `payment.succeeded` — identical event id, identical signature",
            "Exactly-once from at-least-once delivery: the second insert loses on the event-id unique index."),
        new(
            PaymentSimulatorScenario.Delay,
            MagicCentsFor(PaymentSimulatorScenario.Delay),
            "PendingSettlement",
            "1 x `payment.succeeded`, after `SettlementDelay`",
            "The ordinary asynchronous path. The UI must say \"confirming payment\" rather than spin."),
        new(
            PaymentSimulatorScenario.Reorder,
            MagicCentsFor(PaymentSimulatorScenario.Reorder),
            "PendingSettlement",
            "`payment.succeeded` (raised 2nd) delivered first, `payment.authorized` (raised 1st) after `SettlementDelay`",
            "Out-of-order delivery is resolved by the order state machine refusing backwards edges, not by arrival order.")
    ];

    /// <summary>
    /// Picks the scenario for a request. Hint first, amount second, <c>Succeed</c> otherwise.
    /// <para>
    /// The hint wins because it is unambiguous and because the Demo Lab's permalinks depend on it.
    /// The amount is the fallback that lets someone drive a scenario from a plain HTTP client, a
    /// Bruno request or a shopping cart, with no extra field to discover.
    /// </para>
    /// </summary>
    /// <param name="request">The authorization being simulated.</param>
    /// <param name="recogniseMagicAmounts">
    /// Whether the amount may select a scenario. See
    /// <see cref="PaymentSimulatorOptions.RecogniseMagicAmounts"/> for the tradeoff.
    /// </param>
    public static PaymentSimulatorScenario Select(PaymentAuthorizationRequest request, bool recogniseMagicAmounts)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TryParseHint(request.ScenarioHint, out var hinted))
            return hinted;

        if (recogniseMagicAmounts && MagicCents.TryGetValue(TrailingMinorUnits(request.Amount), out var byAmount))
            return byAmount;

        return PaymentSimulatorScenario.Succeed;
    }

    /// <summary>
    /// Parses a scenario name, case-insensitively. Returns <see langword="false"/> for an absent,
    /// unrecognised or purely numeric hint — the last of those because <c>Enum.TryParse</c> would
    /// happily read "3" as <see cref="PaymentSimulatorScenario.Duplicate"/>, tying a public API
    /// contract to the underlying numbering.
    /// </summary>
    public static bool TryParseHint(string? hint, out PaymentSimulatorScenario scenario)
    {
        scenario = default;

        if (string.IsNullOrWhiteSpace(hint))
            return false;

        var trimmed = hint.Trim();
        if (trimmed.All(char.IsAsciiDigit))
            return false;

        return Enum.TryParse(trimmed, ignoreCase: true, out scenario) && Enum.IsDefined(scenario);
    }

    /// <summary>
    /// Renders <see cref="Descriptors"/> as a GitHub-flavoured markdown table. Used to generate
    /// the committed <c>PAYMENT-SCENARIOS.md</c>, and available to the storefront so the Demo Lab
    /// can show the same table without a second copy of the text.
    /// </summary>
    public static string ToMarkdownTable()
    {
        var builder = new StringBuilder()
            .AppendLine("| Scenario | Trigger by hint | Trigger by amount | Authorization result | Webhooks | What it demonstrates |")
            .AppendLine("|---|---|---|---|---|---|");

        foreach (var descriptor in Descriptors)
        {
            builder.Append("| `").Append(descriptor.Scenario).Append("` | `")
                .Append(descriptor.Scenario).Append("` | ")
                .Append(descriptor.AmountTrigger).Append(" | ")
                .Append(descriptor.AuthorizationResult).Append(" | ")
                .Append(descriptor.Webhooks).Append(" | ")
                .Append(descriptor.Demonstrates).AppendLine(" |");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The last two minor units of an amount, which is what the magic-amount table keys on.
    /// Uses the absolute value so the mapping cannot be reached by a negative amount — though the
    /// request already refuses those.
    /// </summary>
    private static long TrailingMinorUnits(Money amount) => Math.Abs(amount.Amount) % 100;

    private static string MagicCentsFor(PaymentSimulatorScenario scenario)
    {
        foreach (var (cents, mapped) in MagicCents)
        {
            if (mapped == scenario)
                return string.Create(CultureInfo.InvariantCulture, $"total ends in `.{cents:D2}`");
        }

        return "any other total";
    }
}
