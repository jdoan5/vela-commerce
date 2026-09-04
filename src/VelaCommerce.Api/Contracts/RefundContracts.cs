namespace VelaCommerce.Api.Contracts;

/// <summary>
/// A request to give money back.
/// <para>
/// Everything is optional except the idempotency key, and the key may travel in the conventional
/// <c>Idempotency-Key</c> header instead of the body — the same accommodation the checkout makes.
/// An omitted <paramref name="Amount"/> means the whole outstanding balance, which is what a
/// "refund this order" button sends and what avoids a client having to compute captured minus
/// refunded and get it wrong by a cent.
/// </para>
/// </summary>
/// <param name="Amount">
/// Minor units to return. Omit for the full remaining balance. A value larger than what is left is
/// refused rather than clamped: silently refunding less than asked is how a shopper ends up
/// believing they were made whole when they were not.
/// </param>
/// <param name="IdempotencyKey">
/// Unique per refund within this order. Required, in the body or the header. A retry carrying the
/// same key returns the first refund rather than issuing a second.
/// </param>
/// <param name="ScenarioHint">
/// Passed through to the gateway and meaningless to this API. The simulator recognises
/// <c>refund-refused</c>, which is how the "the gateway said no" path is driven from a test or the
/// demo without a real acquirer.
/// </param>
public sealed record RefundRequest(
    long? Amount,
    string? IdempotencyKey,
    string? ScenarioHint);

/// <summary>
/// A request to cancel an order, returning any money it has already taken.
/// <para>
/// No amount, deliberately. A cancellation refunds everything outstanding or it does not happen;
/// a partial refund alongside a cancellation would leave a finished order that still owes, and
/// there is no status that means that.
/// </para>
/// </summary>
/// <param name="IdempotencyKey">Unique per cancellation within this order. Required, in the body or the header.</param>
/// <param name="ScenarioHint">Passed through to the gateway, exactly as on <see cref="RefundRequest"/>.</param>
public sealed record CancelOrderRequest(
    string? IdempotencyKey,
    string? ScenarioHint);

/// <summary>
/// What happened to the money, and where the order stands now.
/// <para>
/// Returned by both the refund and the cancellation endpoints, because both answer the same
/// question. The whole ledger comes back rather than just the new row, so a client never has to
/// merge a response into a list it is holding — the list it is holding is what this replaces.
/// </para>
/// </summary>
/// <param name="OrderNumber">The order this refund belongs to.</param>
/// <param name="Status">The order's status after the operation, as the state machine has it.</param>
/// <param name="Captured">What was originally taken. Unchanged by refunding.</param>
/// <param name="Refunded">Running total returned, which the database will not let exceed <paramref name="Captured"/>.</param>
/// <param name="RefundableRemaining">Captured minus refunded. Zero when the shopper has been made whole.</param>
/// <param name="FullyRefunded">True when every captured cent is back. Distinct from the order being cancelled.</param>
/// <param name="RestockedUnits">Units this operation put back on the shelf. Zero for an ordinary refund.</param>
/// <param name="Replayed">
/// True when this idempotency key had already been used and no second refund was issued. The
/// figures below are the first refund's, not a new one — a client showing "refunded!" twice is
/// telling the truth both times about the same money.
/// </param>
/// <param name="Refunds">Every refund on this order, oldest first.</param>
public sealed record RefundResponse(
    string OrderNumber,
    string Status,
    MoneyDto Captured,
    MoneyDto Refunded,
    MoneyDto RefundableRemaining,
    bool FullyRefunded,
    int RestockedUnits,
    bool Replayed,
    IReadOnlyList<RefundLedgerEntry> Refunds);

/// <summary>
/// One movement of money back, as the ledger holds it.
/// </summary>
/// <param name="Amount">How much went back.</param>
/// <param name="Reason">CustomerRequest or Cancellation.</param>
/// <param name="GatewayReference">The gateway's own identifier for this refund, for reconciliation.</param>
/// <param name="RestockedUnits">Units this refund returned to the shelf.</param>
/// <param name="RefundedAt">When it was recorded, which is after the gateway confirmed it.</param>
public sealed record RefundLedgerEntry(
    MoneyDto Amount,
    string Reason,
    string GatewayReference,
    int RestockedUnits,
    DateTimeOffset RefundedAt);
