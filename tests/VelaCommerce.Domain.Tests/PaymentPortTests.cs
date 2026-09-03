using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Payments;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// The payment port's value types, which exist to make one class of bug unwritable.
/// <para>
/// A gateway result is five loosely related fields, and left as a plain bag of nullables it is
/// only a matter of time before something constructs a "succeeded" result carrying a decline
/// reason, or a deferred one with no way to correlate the webhook that will settle it. Neither
/// throws, neither fails a test, and both are wrong on the reporting screen weeks later. So the
/// constructor is private, the four factories are the only entrances, and the tests below are
/// about which shapes exist at all rather than about method coverage.
/// </para>
/// </summary>
public sealed class PaymentPortTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 3, 14, 8, 30, 0, TimeSpan.Zero);

    private static readonly Money Total = new(2_680L);

    private const string Reference = "sim_9f2c4a1b8d3e";

    private static PaymentAuthorizationRequest Request(
        long amountMinorUnits = 2_680L,
        string? scenarioHint = null) =>
        new(new Money(amountMinorUnits), "VC-2001", "idempotency-2001", RequestedAt, scenarioHint);

    [Fact]
    public void A_request_keeps_the_amount_reference_key_and_instant_it_was_given()
    {
        var request = Request();

        Assert.Equal(new Money(2_680L), request.Amount);
        Assert.Equal("VC-2001", request.OrderReference);
        Assert.Equal("idempotency-2001", request.IdempotencyKey);
        Assert.Equal(RequestedAt, request.RequestedAt);
        Assert.Null(request.ScenarioHint);
    }

    /// <summary>
    /// Zero is the interesting half. A negative charge is obviously nonsense, but a zero-amount
    /// authorization looks harmless and is not: it succeeds at every real gateway, captures
    /// nothing, and leaves an order that believes it has been paid for.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-2_680L)]
    public void A_payment_cannot_be_authorized_for_a_zero_or_negative_amount(long amountMinorUnits)
    {
        Assert.Throws<DomainException>(() => Request(amountMinorUnits));
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("   ", "key")]
    [InlineData("VC-2001", "")]
    [InlineData("VC-2001", "   ")]
    public void A_payment_cannot_be_authorized_without_an_order_reference_and_an_idempotency_key(
        string orderReference,
        string idempotencyKey)
    {
        Assert.Throws<DomainException>(() =>
            new PaymentAuthorizationRequest(Total, orderReference, idempotencyKey, RequestedAt));
    }

    /// <summary>
    /// A hint that survives as whitespace would fail to match any scenario and fall through to the
    /// default silently, which is the least debuggable outcome available. Absent, empty and blank
    /// are collapsed into one state so downstream code has one case to handle, not three.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_scenario_hint_is_the_same_as_no_hint(string? hint)
    {
        Assert.Null(Request(scenarioHint: hint).ScenarioHint);
    }

    [Fact]
    public void A_scenario_hint_is_trimmed_but_otherwise_left_alone_because_the_domain_does_not_interpret_it()
    {
        Assert.Equal("Duplicate", Request(scenarioHint: "  Duplicate  ").ScenarioHint);
    }

    [Fact]
    public void A_successful_authorization_carries_the_captured_amount_and_nothing_else()
    {
        var result = PaymentAuthorizationResult.Succeeded(Reference, Total);

        Assert.Equal(PaymentOutcome.Succeeded, result.Outcome);
        Assert.Equal(Total, result.Amount);
        Assert.True(result.IsCaptured);
        Assert.False(result.AwaitsSettlement);
        Assert.Null(result.DeclineReason);
        Assert.Null(result.SettlementCorrelationId);
    }

    [Fact]
    public void A_decline_carries_a_reason_and_is_not_treated_as_captured()
    {
        var result = PaymentAuthorizationResult.Declined(Reference, Total, PaymentDeclineReason.InsufficientFunds);

        Assert.Equal(PaymentOutcome.Declined, result.Outcome);
        Assert.Equal(PaymentDeclineReason.InsufficientFunds, result.DeclineReason);
        Assert.False(result.IsCaptured);
        Assert.False(result.AwaitsSettlement);
        Assert.Null(result.SettlementCorrelationId);
    }

    /// <summary>
    /// Abandonment is not a decline. Nobody refused the payment, so there is no reason to show a
    /// shopper and nothing to retry — the distinction is what stops the UI telling someone their
    /// card was declined when they simply closed the tab.
    /// </summary>
    [Fact]
    public void An_abandoned_payment_has_no_decline_reason_because_nobody_said_no()
    {
        var result = PaymentAuthorizationResult.Abandoned(Reference, Total);

        Assert.Equal(PaymentOutcome.Abandoned, result.Outcome);
        Assert.Null(result.DeclineReason);
        Assert.Null(result.SettlementCorrelationId);
        Assert.False(result.IsCaptured);
        Assert.False(result.AwaitsSettlement);
    }

    [Fact]
    public void A_deferred_authorization_carries_the_handle_its_webhook_will_arrive_with()
    {
        var result = PaymentAuthorizationResult.PendingSettlement(Reference, Total, "set_ab12cd34");

        Assert.Equal(PaymentOutcome.PendingSettlement, result.Outcome);
        Assert.Equal("set_ab12cd34", result.SettlementCorrelationId);
        Assert.True(result.AwaitsSettlement);

        // The distinction the checkout handler turns on: accepted is not the same as paid.
        Assert.False(result.IsCaptured);
    }

    /// <summary>
    /// Without a correlation id a deferred payment is unfindable: the settlement arrives minutes
    /// later, possibly after the container has scaled to zero and come back, and the only thing
    /// tying it to an order is the handle recorded at this moment.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_deferred_authorization_without_a_correlation_id_cannot_be_built(string correlationId)
    {
        Assert.Throws<DomainException>(() =>
            PaymentAuthorizationResult.PendingSettlement(Reference, Total, correlationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_result_without_a_gateway_reference_cannot_be_built_because_it_could_not_be_traced(string reference)
    {
        Assert.Throws<DomainException>(() => PaymentAuthorizationResult.Succeeded(reference, Total));
        Assert.Throws<DomainException>(() => PaymentAuthorizationResult.Abandoned(reference, Total));
        Assert.Throws<DomainException>(() =>
            PaymentAuthorizationResult.Declined(reference, Total, PaymentDeclineReason.DoNotHonor));
    }

    /// <summary>
    /// The closed-set guarantee, stated as a test rather than as a comment.
    /// <para>
    /// Adding a fifth outcome to the enum without giving it a factory would leave a value the
    /// checkout can switch on and nothing can produce — which reads as dead code and is in fact a
    /// hole. This fails on the day the member is added, which is the only day anyone will
    /// remember why.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_outcome_in_the_enum_is_reachable_through_a_factory()
    {
        PaymentAuthorizationResult[] all =
        [
            PaymentAuthorizationResult.Succeeded(Reference, Total),
            PaymentAuthorizationResult.Declined(Reference, Total, PaymentDeclineReason.DoNotHonor),
            PaymentAuthorizationResult.Abandoned(Reference, Total),
            PaymentAuthorizationResult.PendingSettlement(Reference, Total, "set_ab12cd34")
        ];

        Assert.Equal(
            Enum.GetValues<PaymentOutcome>().Order().ToArray(),
            all.Select(static result => result.Outcome).Order().ToArray());
    }

    /// <summary>
    /// A decline reason has no <c>None</c> member, so "declined for no reason" is not a value that
    /// exists. Stated as a test because the absence of an enum member is exactly the kind of
    /// deliberate decision a later contributor adds back for convenience.
    /// </summary>
    [Fact]
    public void There_is_no_absent_decline_reason_to_pair_with_a_successful_outcome()
    {
        Assert.DoesNotContain("None", Enum.GetNames<PaymentDeclineReason>(), StringComparer.OrdinalIgnoreCase);
    }
}
