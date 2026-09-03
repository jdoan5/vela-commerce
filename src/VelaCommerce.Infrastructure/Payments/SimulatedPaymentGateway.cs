using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VelaCommerce.Domain.Payments;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// The default gateway: a complete, signing, deterministic payment processor that lives in this
/// repository and talks to nothing.
/// <para>
/// It exists so that <c>git clone</c> followed by <c>dotnet run</c> completes a purchase end to
/// end — no account, no API key, no network. That is not a convenience. A portfolio link is
/// supposed to still work in three years, and a demo whose money path runs through a third
/// party's test mode is a demo with an expiry date on it: keys rotate, test modes are deprecated,
/// free tiers are withdrawn, and the failure lands on a stranger clicking a link on a CV.
/// </para>
/// <para>
/// What it is not is a stub. It signs its settlement notifications with HMAC-SHA256 using the
/// same helper the receiver verifies with, so the signature check, the constant-time comparison,
/// the replay window, the event-id dedupe and the out-of-order handling downstream are all
/// exercised by real bytes. A real gateway added later behind
/// <see cref="IPaymentGateway"/> meets code that has already been made to survive duplicate,
/// delayed and reordered deliveries.
/// </para>
/// <para>
/// <b>Determinism.</b> Every identifier is derived from the request with SHA-256, never from a
/// counter, a GUID or <c>string.GetHashCode</c> — the last of which is randomised per process in
/// .NET, so a reference built from it would differ between two runs of the same test. The
/// consequence is the property this whole class is for: the same checkout produces the same
/// gateway reference, the same event ids and the same signature bytes, on any machine, on any run.
/// </para>
/// </summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway, IPaymentSimulator
{
    private readonly PaymentSimulatorOptions _options;
    private readonly ILogger<SimulatedPaymentGateway> _logger;

    /// <summary>Whether the host is in Development, which decides if the public dev secret may be used.</summary>
    private readonly bool _isDevelopment;

    /// <summary>
    /// Warns once, at construction, when the committed development secret is still in use. The
    /// gateway is registered as a singleton, so "once" is a property of the lifetime rather than
    /// of a flag this class would otherwise have to keep.
    /// </summary>
    public SimulatedPaymentGateway(
        PaymentSimulatorOptions options,
        ILogger<SimulatedPaymentGateway> logger,
        bool isDevelopment = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _isDevelopment = isDevelopment;

        if (options.UsesDevelopmentSecret)
        {
            // The value itself is never logged — only the fact that it is the public one.
            _logger.LogWarning(
                "The payment simulator is signing with the committed development secret. Anyone who has read the "
                + "repository can forge a settlement notification. Set {Key} before deploying anywhere real.",
                $"{PaymentSimulatorOptions.SectionName}:{nameof(PaymentSimulatorOptions.SigningSecret)}");
        }
    }

    /// <inheritdoc />
    public Task<PaymentAuthorizationResult> AuthorizeAsync(
        PaymentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        // Refuse outright rather than authorize with a secret that is public in this repository:
        // a settlement signed with it could be forged by anyone who cloned the repo. This is the
        // check that used to live at startup, moved to the path where it actually matters.
        _options.AssertUsable(_isDevelopment);

        // No I/O, so nothing to await — and no `async` keyword either, which would earn a compiler
        // warning for a method that never yields. Cancellation is still honoured rather than
        // ignored, because a caller who cancelled expects a cancelled task, not a result.
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<PaymentAuthorizationResult>(cancellationToken);

        return Task.FromResult(Simulate(request).Authorization);
    }

    /// <inheritdoc />
    public SimulatedAuthorization Simulate(PaymentAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scenario = PaymentScenarioCatalog.Select(request, _options.RecogniseMagicAmounts);
        var gatewayReference = GatewayReference(request);

        _logger.LogInformation(
            "Simulated payment {Scenario} for order {OrderReference} as {GatewayReference} ({Amount}).",
            scenario,
            request.OrderReference,
            gatewayReference,
            request.Amount);

        return scenario switch
        {
            PaymentSimulatorScenario.Succeed => new SimulatedAuthorization(
                PaymentAuthorizationResult.Succeeded(gatewayReference, request.Amount),
                []),

            // A refusal is an answer, not a fault: the result comes back normally and no webhook
            // follows, because nothing is ever going to settle.
            PaymentSimulatorScenario.Decline => new SimulatedAuthorization(
                PaymentAuthorizationResult.Declined(gatewayReference, request.Amount, PaymentDeclineReason.DoNotHonor),
                []),

            // Nobody said no. No capture, no notification, and the reservation is left to lapse on
            // its own TTL rather than being released here — releasing it would assume the shopper
            // is not about to come back and finish.
            PaymentSimulatorScenario.Abandon => new SimulatedAuthorization(
                PaymentAuthorizationResult.Abandoned(gatewayReference, request.Amount),
                []),

            PaymentSimulatorScenario.Duplicate => Deferred(request, gatewayReference, PlanDuplicate),
            PaymentSimulatorScenario.Delay => Deferred(request, gatewayReference, PlanDelayed),
            PaymentSimulatorScenario.Reorder => Deferred(request, gatewayReference, PlanReordered),

            _ => throw new InvalidOperationException(
                $"Unhandled payment scenario {scenario}. A scenario added to the enum must be given a behaviour here "
                + "and a row in PaymentScenarioCatalog.Descriptors.")
        };
    }

    /// <summary>
    /// Shared shape of the three asynchronous scenarios: the authorization comes back deferred
    /// with a correlation id, and <paramref name="plan"/> decides what gets delivered and when.
    /// </summary>
    private SimulatedAuthorization Deferred(
        PaymentAuthorizationRequest request,
        string gatewayReference,
        Func<PaymentAuthorizationRequest, string, string, IReadOnlyList<SignedPaymentNotification>> plan)
    {
        var correlationId = Token("set", gatewayReference, "settlement");

        return new SimulatedAuthorization(
            PaymentAuthorizationResult.PendingSettlement(gatewayReference, request.Amount, correlationId),
            plan(request, gatewayReference, correlationId));
    }

    /// <summary>
    /// One settlement, delivered twice. Both deliveries carry the same event id and therefore the
    /// same payload and the same signature — a genuine duplicate, not a second event that happens
    /// to say the same thing. Anything less would let a receiver "pass" by deduping on content.
    /// </summary>
    private IReadOnlyList<SignedPaymentNotification> PlanDuplicate(
        PaymentAuthorizationRequest request,
        string gatewayReference,
        string correlationId)
    {
        var settled = SucceededEvent(request, gatewayReference, correlationId, sequence: 1);

        return
        [
            Sign(settled, TimeSpan.Zero),
            Sign(settled, _options.SettlementDelay)
        ];
    }

    /// <summary>One settlement, arriving after a pause. The ordinary asynchronous happy path.</summary>
    private IReadOnlyList<SignedPaymentNotification> PlanDelayed(
        PaymentAuthorizationRequest request,
        string gatewayReference,
        string correlationId)
    {
        var settled = SucceededEvent(request, gatewayReference, correlationId, sequence: 1);

        return [Sign(settled, _options.SettlementDelay)];
    }

    /// <summary>
    /// Two events raised in one order and delivered in the other: the capture arrives before the
    /// authorization that logically preceded it.
    /// <para>
    /// The receiver must end up with a paid order regardless, because correctness comes from the
    /// order state machine — <c>Paid -&gt; Paid</c> and any backwards edge are illegal by
    /// construction — and not from the network having preserved sequence. The
    /// <see cref="PaymentSettlementEvent.Sequence"/> field lets the receiver notice and log the
    /// inversion; it must never be what the decision depends on, since a real provider promises
    /// no such field.
    /// </para>
    /// </summary>
    private IReadOnlyList<SignedPaymentNotification> PlanReordered(
        PaymentAuthorizationRequest request,
        string gatewayReference,
        string correlationId)
    {
        var authorized = BuildEvent(
            request, gatewayReference, correlationId, PaymentSettlementEvent.AuthorizedType, sequence: 1);

        var settled = SucceededEvent(request, gatewayReference, correlationId, sequence: 2);

        // Raised 1 then 2; delivered 2 then 1.
        return
        [
            Sign(settled, TimeSpan.Zero),
            Sign(authorized, _options.SettlementDelay)
        ];
    }

    private static PaymentSettlementEvent SucceededEvent(
        PaymentAuthorizationRequest request,
        string gatewayReference,
        string correlationId,
        int sequence) =>
        BuildEvent(request, gatewayReference, correlationId, PaymentSettlementEvent.SucceededType, sequence);

    /// <summary>
    /// Builds an event whose id is derived from its own content, so two events of the same type in
    /// the same sequence position for the same payment are the same event.
    /// <para>
    /// <c>OccurredAt</c> is the authorization instant for every event in a plan, not a staggered
    /// series of timestamps. Ordering is carried by <c>Sequence</c> alone, which is the honest
    /// model: timestamps from a distributed system tell you nothing reliable about arrival order,
    /// and a receiver that sorted by them would be trusting the wrong thing.
    /// </para>
    /// </summary>
    private static PaymentSettlementEvent BuildEvent(
        PaymentAuthorizationRequest request,
        string gatewayReference,
        string correlationId,
        string eventType,
        int sequence) =>
        new()
        {
            EventId = Token("evt", gatewayReference, eventType, sequence.ToString(CultureInfo.InvariantCulture)),
            EventType = eventType,
            GatewayReference = gatewayReference,
            OrderReference = request.OrderReference,
            SettlementCorrelationId = correlationId,
            Amount = request.Amount.Amount,
            Currency = request.Amount.Currency,
            Sequence = sequence,
            OccurredAt = request.RequestedAt
        };

    /// <summary>
    /// Serializes an event once and signs those exact bytes. The payload string on the returned
    /// notification is what must be transmitted; re-serializing the event at the sending end would
    /// produce different bytes and a signature that fails for no security reason at all.
    /// </summary>
    private SignedPaymentNotification Sign(PaymentSettlementEvent settlementEvent, TimeSpan deliverAfter)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(settlementEvent, PaymentSettlementEvent.SerializerOptions);

        return new SignedPaymentNotification(
            settlementEvent,
            Encoding.UTF8.GetString(payload),
            PaymentSignature.CreateHeader(payload, settlementEvent.OccurredAt, _options.SigningSecret),
            deliverAfter);
    }

    /// <summary>
    /// The gateway's reference for a payment attempt, derived from the order and the idempotency
    /// key and from nothing else.
    /// <para>
    /// Excluding the amount is what implements the idempotency promise in
    /// <see cref="IPaymentGateway"/>: a double-submitted checkout produces the same reference
    /// twice, so the second call is recognisably the same payment rather than a new one.
    /// </para>
    /// </summary>
    private string GatewayReference(PaymentAuthorizationRequest request) =>
        Token(_options.GatewayReferencePrefix, request.OrderReference, request.IdempotencyKey);

    /// <summary>
    /// A stable, opaque identifier: <c>{prefix}_{24 hex characters of SHA-256 over the parts}</c>.
    /// <para>
    /// SHA-256 rather than <c>string.GetHashCode</c> because that is randomised per process in
    /// .NET — an identifier built from it would change between two runs of the same test and
    /// between two replicas of the same deployment. Truncated to 96 bits, which is not a security
    /// boundary and does not need to be: these are labels for correlating logs, and collision here
    /// would need two different orders sharing an idempotency key.
    /// </para>
    /// <para>
    /// The parts are joined with <c>|</c>, a character no order number or idempotency key in this
    /// system contains, so <c>("ab", "c")</c> and <c>("a", "bc")</c> cannot hash alike.
    /// </para>
    /// </summary>
    private static string Token(string prefix, params string[] parts)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();

        return $"{prefix}_{hex[..24]}";
    }
}
