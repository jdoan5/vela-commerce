namespace VelaCommerce.Api.Contracts;

/// <summary>
/// What the settlement receiver tells a verified sender it did with one notification.
/// <para>
/// <b>Why a body at all, when the sender only needs a 2xx.</b> Every outcome this record can
/// describe is a 200, because every one of them means "stop retrying": the event was applied, or
/// it was already applied, or it can never be applied. Collapsing them into a bare 200 would be
/// correct on the wire and useless everywhere else — the dispatcher's log would say nothing but
/// "delivered", and the Demo Lab's whole point is showing a reviewer that the *second* delivery of
/// a duplicate did nothing and the order is still Paid exactly once. <see cref="Applied"/> is the
/// field that says so.
/// </para>
/// <para>
/// <b>Nothing here is returned before the signature verifies.</b> An unverified caller gets a
/// fixed ProblemDetails with no order number, no status and no event id, because those three
/// fields are precisely what a probe would be fishing for. Past verification the caller has
/// proved it holds the shared secret, so it is entitled to an answer it can act on.
/// </para>
/// </summary>
/// <param name="EventId">
/// The gateway's id for the event, echoed so a delivery log line can be matched to a receipt
/// without parsing the body that was sent.
/// </param>
/// <param name="Outcome">
/// A stable kebab-case token — <c>settled</c>, <c>duplicate</c>, <c>no-legal-transition</c>,
/// <c>acknowledged</c>, <c>order-not-found</c>, <c>unsupported-event-type</c>. A string rather
/// than an enum member name so that adding an outcome cannot silently change an existing one's
/// spelling, and so the Demo Lab can render it without a lookup table.
/// </param>
/// <param name="Applied">
/// True only when this delivery is the one that moved the order. Every duplicate, replay and
/// out-of-order arrival reports false, which is the assertion an integration test wants to make.
/// </param>
/// <param name="OrderNumber">
/// The order this event referred to, or <see langword="null"/> when the payload named no usable
/// one. Echoed rather than looked up, so it is present even when the order itself is not.
/// </param>
/// <param name="OrderStatus">
/// The order's status after this delivery, or <see langword="null"/> when there is no order to
/// report one for. On a duplicate this is read back deliberately: "your second delivery changed
/// nothing and the order is still Paid" is a far more useful answer than "duplicate".
/// </param>
public sealed record PaymentSettlementAcknowledgement(
    string EventId,
    string Outcome,
    bool Applied,
    string? OrderNumber,
    string? OrderStatus);
