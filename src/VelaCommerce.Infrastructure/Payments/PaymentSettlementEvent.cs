using System.Text.Json;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// The body of a settlement notification: what a gateway posts back once it knows the answer.
/// <para>
/// Shaped like a real provider's event rather than like our order, because the receiver being
/// built next has to do the genuinely awkward work — verify a signature over bytes, dedupe on a
/// provider-assigned id, and refuse to trust arrival order. An envelope that mirrored our own
/// aggregate would let the receiver skip all three and still look finished.
/// </para>
/// <para>
/// <see cref="Amount"/> is minor units and <see cref="Currency"/> is a separate ISO code, because
/// that is what crosses a wire. It is reassembled into a <c>Money</c> at the receiver, where the
/// currency mismatch guard can do its job.
/// </para>
/// </summary>
public sealed record PaymentSettlementEvent
{
    /// <summary>Event type raised when the gateway has taken the money.</summary>
    public const string SucceededType = "payment.succeeded";

    /// <summary>
    /// Event type raised when the gateway has reserved the funds but not yet moved them. Carries
    /// no capture, and exists so the <c>Reorder</c> scenario has two genuinely different events to
    /// deliver in the wrong order.
    /// </summary>
    public const string AuthorizedType = "payment.authorized";

    /// <summary>
    /// The exact serializer settings the payload is written with, exposed so the receiver
    /// deserializes with the same ones.
    /// <para>
    /// Shared rather than reconstructed because the signature covers the bytes these settings
    /// produce. Two independently-configured option objects that differ by a casing policy give a
    /// receiver that reads every property as null and a sender that cannot see why — and if anyone
    /// then "fixes" it by re-serializing before verifying, the signature check quietly stops
    /// checking anything. Made read-only at construction so nothing can mutate it under a
    /// running host.
    /// </para>
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>
    /// The gateway's id for this event. The dedupe key: the receiver inserts it into
    /// <c>processed_webhook_events</c> and applies the order transition in the same transaction,
    /// so a duplicate delivery loses on the unique constraint instead of paying twice.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>Either <see cref="SucceededType"/> or <see cref="AuthorizedType"/>.</summary>
    public required string EventType { get; init; }

    /// <summary>The gateway reference returned by the original authorization.</summary>
    public required string GatewayReference { get; init; }

    /// <summary>Our order number, so the receiver can find the order without a lookup table.</summary>
    public required string OrderReference { get; init; }

    /// <summary>
    /// The correlation id handed back on the deferred authorization result. Redundant with
    /// <see cref="GatewayReference"/> today and kept separate on purpose: a real provider issues
    /// one id for the payment and another for the settlement, and collapsing them here would bake
    /// an assumption into the receiver that a real gateway would then break.
    /// </summary>
    public required string SettlementCorrelationId { get; init; }

    /// <summary>Amount in minor units.</summary>
    public required long Amount { get; init; }

    /// <summary>ISO-4217 code for <see cref="Amount"/>.</summary>
    public required string Currency { get; init; }

    /// <summary>
    /// Position in the sequence the gateway raised these events in, starting at 1. Present so the
    /// receiver can <em>observe</em> that it got them out of order; correctness must not depend on
    /// it, because a real provider does not promise a sequence number at all.
    /// </summary>
    public required int Sequence { get; init; }

    /// <summary>When the gateway says the event happened. Not when it was delivered.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Web defaults — camelCase names, case-insensitive reads — and nothing else. There is no
    /// enum on this payload and no optional member, so there is nothing here that a reviewer has
    /// to hold in their head while reading a signed body.
    /// <para>
    /// No decline type is defined, because no scenario emits one: a refusal is answered
    /// synchronously by the authorization result, so a <c>payment.failed</c> event today would be
    /// a member nothing produces. Add it when something raises it.
    /// </para>
    /// </summary>
    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // populateMissingResolver: true attaches the default reflection-based resolver. Without it
        // MakeReadOnly throws, because freezing options that have no way to describe a type would
        // produce a serializer that can never serialize anything.
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}
