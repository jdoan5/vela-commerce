using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Payments;
using VelaCommerce.Infrastructure.Payments;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The in-repository payment gateway and the signature scheme it shares with the webhook receiver.
/// <para>
/// These need no database and no container — they are unit tests living in the integration project
/// because it is the only test project that references <c>VelaCommerce.Infrastructure</c>. They
/// deliberately carry no <c>[Collection]</c> attribute, so xUnit never starts the PostgreSQL
/// fixture for them. A dedicated <c>VelaCommerce.Infrastructure.Tests</c> project is the better
/// long-term home; creating one means a new csproj, a solution entry and a CI step, which is a
/// change to files this slice does not own.
/// </para>
/// <para>
/// Two properties are load-bearing enough that most of the file is about them. <b>Determinism</b>:
/// a reviewer who clicks "trigger a decline" must get a decline every time, and a failing test must
/// fail again on the next run, so nothing here may depend on a clock, a counter or a GUID.
/// <b>Signature fidelity</b>: the simulator's whole justification is that it makes the receiver do
/// real work, which is only true if the bytes it signs are genuinely verifiable and genuinely
/// unforgeable.
/// </para>
/// </summary>
public sealed class PaymentSimulatorTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 3, 14, 8, 30, 0, TimeSpan.Zero);

    private const string TestSecret = "a-test-signing-secret-of-quite-sufficient-length";

    private static PaymentSimulatorOptions Options(
        string secret = TestSecret,
        bool recogniseMagicAmounts = true) =>
        new() { SigningSecret = secret, RecogniseMagicAmounts = recogniseMagicAmounts };

    private static SimulatedPaymentGateway Gateway(PaymentSimulatorOptions? options = null) =>
        new(options ?? Options(), NullLogger<SimulatedPaymentGateway>.Instance);

    private static PaymentAuthorizationRequest Request(
        long amountMinorUnits = 2_680L,
        string? scenarioHint = null,
        string orderReference = "VC-2001",
        string idempotencyKey = "idempotency-2001") =>
        new(new Money(amountMinorUnits), orderReference, idempotencyKey, RequestedAt, scenarioHint);

    // ---------------------------------------------------------------- choosing a scenario

    [Theory]
    [InlineData("Succeed", PaymentOutcome.Succeeded)]
    [InlineData("decline", PaymentOutcome.Declined)]
    [InlineData("ABANDON", PaymentOutcome.Abandoned)]
    [InlineData("Duplicate", PaymentOutcome.PendingSettlement)]
    [InlineData("Delay", PaymentOutcome.PendingSettlement)]
    [InlineData("  Reorder  ", PaymentOutcome.PendingSettlement)]
    public void A_scenario_hint_selects_the_documented_outcome_whatever_case_it_arrives_in(
        string hint,
        PaymentOutcome expected)
    {
        Assert.Equal(expected, Gateway().Simulate(Request(scenarioHint: hint)).Authorization.Outcome);
    }

    /// <summary>
    /// The published table promises the last two minor units of the total select the scenario. A
    /// reviewer with nothing but a browser and a cart relies on this, so it is asserted against the
    /// same amounts the markdown advertises — and against three different orders of magnitude, to
    /// pin down that only the trailing cents are read.
    /// </summary>
    [Theory]
    [InlineData(2_601L, PaymentSimulatorScenario.Decline)]
    [InlineData(2_602L, PaymentSimulatorScenario.Abandon)]
    [InlineData(2_603L, PaymentSimulatorScenario.Duplicate)]
    [InlineData(2_604L, PaymentSimulatorScenario.Delay)]
    [InlineData(2_605L, PaymentSimulatorScenario.Reorder)]
    [InlineData(101L, PaymentSimulatorScenario.Decline)]
    [InlineData(120_301L, PaymentSimulatorScenario.Decline)]
    [InlineData(2_680L, PaymentSimulatorScenario.Succeed)]
    [InlineData(2_600L, PaymentSimulatorScenario.Succeed)]
    [InlineData(2_699L, PaymentSimulatorScenario.Succeed)]
    public void The_trailing_cents_of_the_total_select_a_scenario_when_no_hint_is_given(
        long amountMinorUnits,
        PaymentSimulatorScenario expected)
    {
        Assert.Equal(
            expected,
            PaymentScenarioCatalog.Select(Request(amountMinorUnits), recogniseMagicAmounts: true));
    }

    [Fact]
    public void An_explicit_hint_beats_the_amount_because_a_permalink_must_not_depend_on_a_cart()
    {
        // .01 says decline; the hint says succeed.
        var result = Gateway().Simulate(Request(2_601L, scenarioHint: "Succeed")).Authorization;

        Assert.Equal(PaymentOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public void Turning_off_magic_amounts_leaves_the_hint_as_the_only_trigger()
    {
        var gateway = Gateway(Options(recogniseMagicAmounts: false));

        Assert.Equal(PaymentOutcome.Succeeded, gateway.Simulate(Request(2_601L)).Authorization.Outcome);
        Assert.Equal(
            PaymentOutcome.Declined,
            gateway.Simulate(Request(2_601L, scenarioHint: "Decline")).Authorization.Outcome);
    }

    /// <summary>
    /// <c>Enum.TryParse</c> would read "3" as <c>Duplicate</c>, quietly making the enum's numbering
    /// part of a public API. A numeric hint is treated as no hint at all.
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void A_numeric_or_unrecognised_hint_is_no_hint(string? hint)
    {
        Assert.False(PaymentScenarioCatalog.TryParseHint(hint, out _));
    }

    [Fact]
    public void Every_scenario_in_the_enum_has_a_published_row_and_a_behaviour()
    {
        Assert.Equal(
            Enum.GetValues<PaymentSimulatorScenario>().Order().ToArray(),
            PaymentScenarioCatalog.Descriptors.Select(static d => d.Scenario).Order().ToArray());

        // And each one actually runs rather than falling into the unhandled-scenario throw.
        foreach (var scenario in Enum.GetValues<PaymentSimulatorScenario>())
            Assert.NotNull(Gateway().Simulate(Request(scenarioHint: scenario.ToString())).Authorization);
    }

    // ---------------------------------------------------------------- the synchronous answers

    [Fact]
    public void A_succeeding_payment_captures_the_full_amount_and_promises_no_webhook()
    {
        var simulated = Gateway().Simulate(Request(scenarioHint: "Succeed"));

        Assert.True(simulated.Authorization.IsCaptured);
        Assert.Equal(new Money(2_680L), simulated.Authorization.Amount);
        Assert.Empty(simulated.Notifications);
    }

    [Fact]
    public void A_decline_comes_back_as_a_result_rather_than_an_exception_and_sends_nothing()
    {
        var simulated = Gateway().Simulate(Request(scenarioHint: "Decline"));

        Assert.Equal(PaymentOutcome.Declined, simulated.Authorization.Outcome);
        Assert.Equal(PaymentDeclineReason.DoNotHonor, simulated.Authorization.DeclineReason);
        Assert.Empty(simulated.Notifications);
    }

    [Fact]
    public void An_abandoned_payment_leaves_no_capture_and_no_notification()
    {
        var simulated = Gateway().Simulate(Request(scenarioHint: "Abandon"));

        Assert.Equal(PaymentOutcome.Abandoned, simulated.Authorization.Outcome);
        Assert.Null(simulated.Authorization.DeclineReason);
        Assert.Empty(simulated.Notifications);
    }

    // ---------------------------------------------------------------- the asynchronous answers

    /// <summary>
    /// A duplicate must be the <em>same</em> event delivered twice — same id, same bytes, same
    /// signature. A second event that merely says the same thing would let a receiver "pass" this
    /// scenario by deduping on content, which is not what a real at-least-once delivery looks like.
    /// </summary>
    [Fact]
    public void The_duplicate_scenario_delivers_one_event_twice_byte_for_byte()
    {
        var simulated = Gateway().Simulate(Request(scenarioHint: "Duplicate"));

        Assert.True(simulated.Authorization.AwaitsSettlement);
        Assert.Equal(2, simulated.Notifications.Count);

        var (first, second) = (simulated.Notifications[0], simulated.Notifications[1]);
        Assert.Equal(first.Event.EventId, second.Event.EventId);
        Assert.Equal(first.Payload, second.Payload);
        Assert.Equal(first.Signature, second.Signature);

        // The second arrives later, which is what makes it a redelivery rather than a double post.
        Assert.Equal(TimeSpan.Zero, first.DeliverAfter);
        Assert.True(second.DeliverAfter > TimeSpan.Zero);
    }

    [Fact]
    public void The_delay_scenario_defers_a_single_settlement_by_the_configured_pause()
    {
        var options = Options() with { SettlementDelay = TimeSpan.FromSeconds(7) };

        var simulated = Gateway(options).Simulate(Request(scenarioHint: "Delay"));

        Assert.True(simulated.Authorization.AwaitsSettlement);
        var notification = Assert.Single(simulated.Notifications);
        Assert.Equal(TimeSpan.FromSeconds(7), notification.DeliverAfter);
        Assert.Equal(PaymentSettlementEvent.SucceededType, notification.Event.EventType);
    }

    /// <summary>
    /// The capture is raised second and delivered first. The receiver must reach a paid order
    /// anyway, because the order state machine refuses backwards edges — not because anything
    /// sorted these by sequence number.
    /// </summary>
    [Fact]
    public void The_reorder_scenario_delivers_the_later_event_first()
    {
        var simulated = Gateway().Simulate(Request(scenarioHint: "Reorder"));

        Assert.Equal(2, simulated.Notifications.Count);

        var delivered = simulated.Notifications.OrderBy(static n => n.DeliverAfter).ToArray();
        Assert.Equal(PaymentSettlementEvent.SucceededType, delivered[0].Event.EventType);
        Assert.Equal(2, delivered[0].Event.Sequence);
        Assert.Equal(PaymentSettlementEvent.AuthorizedType, delivered[1].Event.EventType);
        Assert.Equal(1, delivered[1].Event.Sequence);

        // Two genuinely different events, so a dedupe on event id must not swallow one of them.
        Assert.NotEqual(delivered[0].Event.EventId, delivered[1].Event.EventId);
    }

    [Fact]
    public void A_deferred_authorization_and_its_events_share_the_correlation_id_the_receiver_looks_up()
    {
        var simulated = Gateway().Simulate(Request(scenarioHint: "Delay"));

        var correlationId = simulated.Authorization.SettlementCorrelationId;
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.All(simulated.Notifications, n => Assert.Equal(correlationId, n.Event.SettlementCorrelationId));
    }

    [Fact]
    public void A_settlement_event_carries_the_order_the_amount_and_the_currency_the_payment_was_for()
    {
        var request = Request(4_995L, scenarioHint: "Delay", orderReference: "VC-7788");

        var notification = Assert.Single(Gateway().Simulate(request).Notifications);

        Assert.Equal("VC-7788", notification.Event.OrderReference);
        Assert.Equal(4_995L, notification.Event.Amount);
        Assert.Equal(Money.DefaultCurrency, notification.Event.Currency);
        Assert.Equal(RequestedAt, notification.Event.OccurredAt);
    }

    // ---------------------------------------------------------------- determinism

    /// <summary>
    /// The property the whole class exists for, asserted across two independent instances so that
    /// nothing can be passing by way of a cached field.
    /// </summary>
    [Fact]
    public void The_same_request_produces_the_same_reference_ids_bytes_and_signatures_every_time()
    {
        var first = Gateway().Simulate(Request(scenarioHint: "Reorder"));
        var second = Gateway().Simulate(Request(scenarioHint: "Reorder"));

        Assert.Equal(first.Authorization.GatewayReference, second.Authorization.GatewayReference);
        Assert.Equal(first.Authorization.SettlementCorrelationId, second.Authorization.SettlementCorrelationId);

        Assert.Equal(
            first.Notifications.Select(static n => (n.Event.EventId, n.Payload, n.Signature)),
            second.Notifications.Select(static n => (n.Event.EventId, n.Payload, n.Signature)));
    }

    /// <summary>
    /// The idempotency promise in <c>IPaymentGateway</c>: two authorizations of the same order
    /// under the same key are the same payment, so the reference must not move — not even when the
    /// amount does, which is what a re-priced double submit looks like.
    /// </summary>
    [Fact]
    public void The_gateway_reference_depends_on_the_order_and_the_key_and_not_on_the_amount()
    {
        var gateway = Gateway();

        var original = gateway.Simulate(Request(2_680L)).Authorization.GatewayReference;
        var repriced = gateway.Simulate(Request(9_999L)).Authorization.GatewayReference;
        var otherKey = gateway.Simulate(Request(2_680L, idempotencyKey: "idempotency-9999")).Authorization.GatewayReference;

        Assert.Equal(original, repriced);
        Assert.NotEqual(original, otherKey);
        Assert.StartsWith("sim_", original, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_port_returns_the_same_answer_the_simulator_reached()
    {
        var gateway = Gateway();
        var request = Request(scenarioHint: "Delay");

        var throughPort = await gateway.AuthorizeAsync(request, CancellationToken.None);

        Assert.Equal(gateway.Simulate(request).Authorization, throughPort);
    }

    // ---------------------------------------------------------------- the signature

    [Fact]
    public void A_signed_notification_verifies_against_the_bytes_that_were_signed()
    {
        var options = Options();
        var notification = Assert.Single(Gateway(options).Simulate(Request(scenarioHint: "Delay")).Notifications);

        var result = PaymentSignature.Verify(
            notification.PayloadBytes(),
            notification.Signature,
            options.SigningSecret,
            RequestedAt + options.SettlementDelay,
            options.SignatureTolerance);

        Assert.Equal(PaymentSignatureResult.Valid, result);
    }

    /// <summary>
    /// The attack the signature exists to stop: change the amount in the body and the MAC no longer
    /// matches, so a forged settlement cannot mark an order paid for the wrong figure.
    /// </summary>
    [Fact]
    public void A_tampered_payload_does_not_verify()
    {
        var options = Options();
        var notification = Assert.Single(Gateway(options).Simulate(Request(scenarioHint: "Delay")).Notifications);

        var tampered = Encoding.UTF8.GetBytes(notification.Payload.Replace("2680", "1", StringComparison.Ordinal));

        Assert.Equal(
            PaymentSignatureResult.Mismatched,
            PaymentSignature.Verify(tampered, notification.Signature, options.SigningSecret, RequestedAt, options.SignatureTolerance));
    }

    [Fact]
    public void A_signature_made_with_a_different_secret_does_not_verify()
    {
        var notification = Assert.Single(Gateway().Simulate(Request(scenarioHint: "Delay")).Notifications);

        Assert.Equal(
            PaymentSignatureResult.Mismatched,
            PaymentSignature.Verify(
                notification.PayloadBytes(),
                notification.Signature,
                "a-completely-different-secret-of-sufficient-length",
                RequestedAt,
                TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// The replay window, in both directions. A signature lifted from a log stops working once it
    /// ages out, and one dated in the future is skew or forgery rather than freshness. Reported as
    /// <c>Expired</c> rather than <c>Mismatched</c> so a receiver can tell a replay from a forgery.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    public void A_signature_outside_the_replay_window_is_rejected_as_stale_not_as_forged(int minutesFromNow)
    {
        var options = Options();
        var notification = Assert.Single(Gateway(options).Simulate(Request(scenarioHint: "Delay")).Notifications);

        Assert.Equal(
            PaymentSignatureResult.Expired,
            PaymentSignature.Verify(
                notification.PayloadBytes(),
                notification.Signature,
                options.SigningSecret,
                RequestedAt.AddMinutes(minutesFromNow),
                options.SignatureTolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=1772668800")]                                   // no signature
    [InlineData("v1=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // no timestamp
    [InlineData("t=not-a-number,v1=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("t=1772668800,v1=tooshort")]
    [InlineData("t=1772668800,v1=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // not hex
    public void A_header_that_is_not_in_the_documented_shape_is_reported_as_malformed(string? header)
    {
        Assert.Equal(
            PaymentSignatureResult.Malformed,
            PaymentSignature.Verify("{}"u8, header, TestSecret, RequestedAt, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// The labelled fields exist so a future <c>v2</c> can be sent alongside <c>v1</c> and
    /// receivers migrated one at a time. A receiver that rejected unknown fields would make that
    /// migration a flag day.
    /// </summary>
    [Fact]
    public void An_unknown_signature_field_is_ignored_rather_than_rejected()
    {
        var header = PaymentSignature.CreateHeader("{}"u8, RequestedAt, TestSecret) + ",v2=something-later";

        Assert.Equal(
            PaymentSignatureResult.Valid,
            PaymentSignature.Verify("{}"u8, header, TestSecret, RequestedAt, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Signing_the_same_bytes_at_the_same_instant_is_reproducible()
    {
        Assert.Equal(
            PaymentSignature.CreateHeader("{\"a\":1}"u8, RequestedAt, TestSecret),
            PaymentSignature.CreateHeader("{\"a\":1}"u8, RequestedAt, TestSecret));
    }

    /// <summary>
    /// Timestamp and payload are both inside the MAC, so a signature cannot be re-dated to escape
    /// the replay window.
    /// </summary>
    [Fact]
    public void Moving_the_timestamp_changes_the_signature()
    {
        Assert.NotEqual(
            PaymentSignature.Compute("{\"a\":1}"u8, RequestedAt.ToUnixTimeSeconds(), TestSecret),
            PaymentSignature.Compute("{\"a\":1}"u8, RequestedAt.AddSeconds(1).ToUnixTimeSeconds(), TestSecret));
    }

    [Fact]
    public void Signature_comparison_accepts_either_case_and_refuses_a_wrong_length()
    {
        var signature = PaymentSignature.Compute("{}"u8, RequestedAt.ToUnixTimeSeconds(), TestSecret);

        Assert.True(PaymentSignature.FixedTimeEquals(signature, signature.ToUpperInvariant()));
        Assert.False(PaymentSignature.FixedTimeEquals(signature, signature[..63]));
        Assert.False(PaymentSignature.FixedTimeEquals(signature, null));
    }

    // ---------------------------------------------------------------- the payload on the wire

    /// <summary>
    /// The receiver deserializes with the same shared options the sender wrote with. Asserted here
    /// because the failure mode is quiet: a mismatched casing policy gives a receiver that reads
    /// every property as null, and "fixing" it by re-serializing before verifying would silently
    /// disable the signature check.
    /// </summary>
    [Fact]
    public void The_payload_round_trips_through_the_shared_serializer_settings()
    {
        var notification = Assert.Single(Gateway().Simulate(Request(scenarioHint: "Delay")).Notifications);

        var received = JsonSerializer.Deserialize<PaymentSettlementEvent>(
            notification.Payload,
            PaymentSettlementEvent.SerializerOptions);

        Assert.Equal(notification.Event, received);
    }

    // ---------------------------------------------------------------- configuration

    /// <summary>
    /// The secret must not reach a log, and a convention that "nobody logs the options" holds until
    /// the first person who does. Records print every member by default, so the print hook is
    /// overridden — this is the test that keeps it overridden.
    /// </summary>
    [Fact]
    public void The_options_never_print_the_signing_secret()
    {
        var rendered = Options("a-very-secret-value-of-quite-sufficient-length").ToString();

        Assert.DoesNotContain("a-very-secret-value", rendered, StringComparison.Ordinal);
        Assert.Contains("redacted", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_committed_development_secret_is_refused_outside_development()
    {
        var options = new PaymentSimulatorOptions();

        Assert.True(options.UsesDevelopmentSecret);

        // Composing a host is allowed with the public secret, because the build-time OpenAPI
        // generator composes this host as Production and refusing there broke the build rather
        // than the deployment. What must never happen is taking money with a secret anyone can
        // read, so the refusal moved to AssertUsable, which the gateway calls before authorizing.
        options.Validate(isDevelopment: false);
        options.AssertUsable(isDevelopment: true);
        Assert.Throws<InvalidOperationException>(() => options.AssertUsable(isDevelopment: false));
    }

    /// <summary>
    /// The hole this closes, and why the obvious guard missed it.
    /// <para>
    /// <c>infra/variables.tf</c> applies with a placeholder signing secret on purpose, so that no
    /// live key ever enters Terraform state; the operator is expected to replace it out-of-band
    /// with <c>az containerapp secret set</c>. The guard checked one string — the committed
    /// development default — so the placeholder sailed through it: long enough to pass the length
    /// check, different enough to pass the equality check, and published in a public repository.
    /// A deploy that skipped that one manual step would have signed real settlement notifications
    /// with a key any reader could copy, and nothing in the system would have objected.
    /// </para>
    /// </summary>
    [Fact]
    public void The_terraform_placeholder_secret_is_refused_outside_development()
    {
        var options = Options(PaymentSimulatorOptions.TerraformPlaceholderSecret);

        Assert.False(options.UsesDevelopmentSecret);
        Assert.True(options.UsesPubliclyKnownSecret);

        // Composing a host is still allowed, for the reason the development-default test gives.
        options.Validate(isDevelopment: false);
        options.AssertUsable(isDevelopment: true);

        var refusal = Assert.Throws<InvalidOperationException>(() => options.AssertUsable(isDevelopment: false));
        Assert.Contains("Terraform placeholder", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The application duplicates the placeholder literal from <c>infra/variables.tf</c>, which is
    /// a coupling accepted deliberately — the alternative is a guard whose correctness depends on
    /// two files agreeing by memory. This is the test that makes them agree by CI instead. If it
    /// fails, the Terraform default changed and the guard has stopped covering it.
    /// </summary>
    [Fact]
    public void The_placeholder_the_guard_refuses_is_the_one_terraform_actually_applies()
    {
        var variables = File.ReadAllText(RepoFile("infra/variables.tf"));

        var occurrences = variables.Split(
            $"default     = \"{PaymentSimulatorOptions.TerraformPlaceholderSecret}\"",
            StringSplitOptions.None).Length - 1;

        Assert.True(
            occurrences > 0,
            $"infra/variables.tf no longer defaults any variable to "
            + $"'{PaymentSimulatorOptions.TerraformPlaceholderSecret}'. Either the placeholder changed - in which "
            + "case PaymentSimulatorOptions.TerraformPlaceholderSecret must change with it, or the money path will "
            + "sign with a public value it does not recognise - or the default was removed, in which case delete "
            + "this test and the constant together.");
    }

    /// <summary>
    /// Walks up from the test binary to the repository root. The suite runs from
    /// <c>bin/Debug/net10.0</c>, and hard-coding a relative depth breaks the moment the target
    /// framework or configuration changes.
    /// </summary>
    private static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VelaCommerce.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, relativePath);
    }

    /// <summary>
    /// The two delays are not independent: a notification is signed at authorization and delivered
    /// later, so a delay reaching the tolerance means every deferred settlement arrives already
    /// expired. That looks like a signature vulnerability and is a misconfiguration.
    /// </summary>
    [Fact]
    public void A_settlement_delay_that_outlives_the_replay_window_is_refused_at_startup()
    {
        var options = Options() with
        {
            SettlementDelay = TimeSpan.FromMinutes(5),
            SignatureTolerance = TimeSpan.FromMinutes(5)
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));
    }

    [Fact]
    public void A_secret_short_enough_to_weaken_the_mac_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Options("too-short").Validate(isDevelopment: true));
    }

    [Fact]
    public void Configuration_overrides_the_defaults_and_absent_keys_keep_them()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:Simulator:SettlementDelay"] = "00:00:07",
                ["Payments:Simulator:RecogniseMagicAmounts"] = "false"
            })
            .Build();

        var options = PaymentSimulatorOptions.FromConfiguration(
            configuration.GetSection(PaymentSimulatorOptions.SectionName));

        Assert.Equal(TimeSpan.FromSeconds(7), options.SettlementDelay);
        Assert.False(options.RecogniseMagicAmounts);
        Assert.Equal(TimeSpan.FromMinutes(5), options.SignatureTolerance);
        Assert.True(options.UsesDevelopmentSecret);
    }

    [Fact]
    public void An_unparseable_configured_value_names_its_own_key_rather_than_binding_to_default()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:Simulator:SettlementDelay"] = "three seconds"
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            PaymentSimulatorOptions.FromConfiguration(configuration.GetSection(PaymentSimulatorOptions.SectionName)));

        Assert.Contains("SettlementDelay", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public void The_simulator_is_registered_once_behind_the_port_the_seam_and_the_concrete_type()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPaymentSimulator(new ConfigurationBuilder().Build(), isDevelopment: true);

        using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IPaymentGateway>();
        var seam = provider.GetRequiredService<IPaymentSimulator>();

        Assert.IsType<SimulatedPaymentGateway>(port);

        // One object, three registrations: two instances would drift the moment either was
        // reconfigured, and would sign with different secrets without anything saying so.
        Assert.Same(port, seam);
        Assert.Same(port, provider.GetRequiredService<SimulatedPaymentGateway>());
    }

    /// <summary>
    /// Environment is inferred from configuration and defaults to Production — the host's own
    /// default — so a deployment that forgot to set the variable refuses to take money rather than
    /// signing real settlements with a secret published in this repository.
    /// <para>
    /// It does not fail to start, and the difference matters to whoever deploys it: the host comes
    /// up, serves the shop and passes a health probe, and the refusal lands on the first checkout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Authorizing_refuses_the_development_secret_when_nothing_says_this_is_development()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Registration itself must succeed — see the note on the options test above. The refusal
        // is on the money path, so prove it there: the gateway will not authorize.
        services.AddPaymentSimulator(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IPaymentGateway>();

        var request = new PaymentAuthorizationRequest(
            new Money(1_000),
            "VELA-TEST",
            "idempotency-test",
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AuthorizeAsync(request));
    }

    [Fact]
    public void Registration_reads_the_environment_from_configuration_when_the_host_does_not_say()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPaymentSimulator(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IPaymentGateway>());
    }
}
