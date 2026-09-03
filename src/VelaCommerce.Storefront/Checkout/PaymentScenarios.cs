namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// One path the payment simulator can take, described for a shopper rather than for a maintainer.
/// </summary>
/// <param name="Hint">
/// The value sent as <c>paymentScenario</c>. It must match the simulator's enum name exactly —
/// parsing is case-insensitive but not fuzzy — which is why it is kept apart from
/// <see cref="Label"/> rather than derived from it.
/// </param>
/// <param name="Label">The plain-language name on the radio button. What the outcome is, not what the enum is called.</param>
/// <param name="Summary">One sentence saying what will happen and, where it matters, what happens to the cart and the stock.</param>
/// <param name="AmountTrigger">
/// The trailing cents that select this path when no hint is sent, quoted from the simulator's own
/// table. Shown in a disclosure because it is the answer to "how do I trigger this from Bruno or
/// curl", which is a question a reviewer of an API demo actually has.
/// </param>
public sealed record PaymentScenarioOption(string Hint, string Label, string Summary, string AmountTrigger);

/// <summary>
/// The scenario picker's contents.
///
/// <para>
/// <strong>Why this list is hand-written rather than fetched or referenced.</strong>
/// <c>PaymentScenarioCatalog</c> is the source of truth and it lives in Infrastructure, behind EF
/// Core and a database driver. The storefront is a standalone WebAssembly app with no project
/// reference to anything on the server, deliberately: the shop must open with the API and the
/// database switched off, and a reference here would drag the whole server stack into the browser
/// download. Fetching the table instead would put a network call on a page that has to work while
/// the API is still waking up.
/// </para>
/// <para>
/// So the copy is duplicated, and the duplication is bounded: <see cref="PaymentScenarioOption.Hint"/>
/// is the only value the server actually reads, an unrecognised hint is ignored rather than fatal
/// (the simulator falls back to the amount, then to Succeed), and the prose beside it is written for
/// a shopper anyway — "Card declined" is not a phrase the maintainer-facing table contains. The
/// generated table in <c>src/VelaCommerce.Infrastructure/Payments/PAYMENT-SCENARIOS.md</c> stays the
/// document of record, and is what to check this file against when a scenario is added.
/// </para>
/// </summary>
public static class PaymentScenarios
{
    /// <summary>The scenario the picker starts on. A shopper who never touches the control buys something.</summary>
    public const string DefaultHint = "Succeed";

    /// <summary>
    /// Every scenario, in the order the simulator's own table reads: the three synchronous answers
    /// first, then the three ways an asynchronous settlement can misbehave.
    /// </summary>
    public static IReadOnlyList<PaymentScenarioOption> All { get; } =
    [
        new(
            "Succeed",
            "Succeeds",
            "The card is accepted and the full amount is captured inside the checkout request. The order is paid before the page has finished loading.",
            "any other total"),
        new(
            "Decline",
            "Card declined",
            "The bank refuses. The order is cancelled and its stock released — and your cart is left exactly as it was, so you can change something and try again.",
            "total ends in .01"),
        new(
            "Abandon",
            "Payment never finished",
            "Nobody refuses and nobody pays. The order is created, stays pending, and holds its reserved stock until the reservation lapses on its own.",
            "total ends in .02"),
        new(
            "Duplicate",
            "Bank confirms twice",
            "The same signed confirmation is delivered twice with the same event id. The second one loses on a unique index, so the order is paid exactly once.",
            "total ends in .03"),
        new(
            "Delay",
            "Settles after a delay",
            "Authorised now, captured a few seconds later by a signed webhook. Until it lands the order is genuinely not paid, and the order page says so rather than showing a receipt.",
            "total ends in .04"),
        new(
            "Reorder",
            "Confirmations arrive out of order",
            "The later confirmation is delivered before the earlier one. The order refuses to move backwards, so the timeline still reads correctly.",
            "total ends in .05"),
    ];

    /// <summary>
    /// The option for a hint, or the default when the hint is unknown. Never null, because the
    /// picker always has something selected and a null selection would render as no radio checked.
    /// </summary>
    public static PaymentScenarioOption Find(string? hint)
    {
        foreach (var option in All)
        {
            if (string.Equals(option.Hint, hint, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return All[0];
    }
}
