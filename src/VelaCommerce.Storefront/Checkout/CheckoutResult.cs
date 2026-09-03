namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// Every documented answer <c>POST /api/checkout</c> can give, as a closed set the UI must handle.
/// <para>
/// The endpoint answers with six different status codes and two of them mean success. Collapsing
/// that into "worked / did not work" is how a shopper ends up being shown a receipt for a payment
/// that has not settled, or being told to try again with a key that already belongs to a cancelled
/// order. So each one is named here, the client maps status codes onto these names once, and the
/// page renders a distinct, honest message per name.
/// </para>
/// </summary>
public enum CheckoutOutcome
{
    /// <summary>201. The order exists and the money moved inside the request.</summary>
    Placed,

    /// <summary>
    /// 202. The order exists, the gateway accepted it, and settlement arrives later by signed
    /// webhook. Emphatically not the same as <see cref="Placed"/>: nothing has been captured yet.
    /// </summary>
    Settling,

    /// <summary>
    /// 200. This exact idempotency key had already created an order and the server handed that
    /// same order back. No second charge, no second order number — the key doing its job.
    /// </summary>
    AlreadyPlaced,

    /// <summary>
    /// 402, gateway said no. The order was cancelled and its stock released; the cart survives, so
    /// the shopper can try again — but with a <em>new</em> key, because this one now belongs to the
    /// cancelled order.
    /// </summary>
    Declined,

    /// <summary>
    /// 402, nobody said no. The gateway was abandoned mid-flow, or the key belongs to an earlier
    /// order that never paid. The order row exists and is Pending, holding reserved stock until its
    /// reservation lapses.
    /// </summary>
    NotCompleted,

    /// <summary>
    /// 409 with <c>priceChanges</c>. One or more lines no longer cost what the cart says, or have
    /// left the catalog. Nothing was created and nothing was charged.
    /// </summary>
    PriceMoved,

    /// <summary>
    /// 409 with <c>shortfall</c>. One line lost the race for the last unit. Nothing was created and
    /// nothing was charged.
    /// </summary>
    OutOfStock,

    /// <summary>
    /// 400. The server refused the request itself — a broken address, a missing or ambiguous key,
    /// an empty cart. Nothing was created.
    /// </summary>
    Rejected,

    /// <summary>
    /// No usable answer: a timeout, a dropped connection, a 5xx, or a body this build cannot read.
    /// <para>
    /// <strong>The state of the world is unknown, and that is the honest thing to say.</strong> The
    /// order may exist and may even be paid. This is the case the idempotency key was invented for:
    /// retrying with the same key either creates the order or hands back the one already created,
    /// and cannot produce two.
    /// </para>
    /// </summary>
    Interrupted,
}

/// <summary>
/// The answer to one checkout attempt, with everything the page needs to render it and to decide
/// what the next attempt should look like.
/// </summary>
/// <param name="Outcome">Which of the documented answers this was.</param>
/// <param name="Order">The order, on any of the three successful outcomes. Null otherwise.</param>
/// <param name="Problem">
/// The server's own problem document, when it sent one. Its wording is preferred over anything the
/// storefront could re-derive: "Country must be an ISO alpha-2 code" is a better error than a
/// generic "check your address", and it is the domain's own sentence.
/// </param>
/// <param name="Detail">
/// A technical line for the disclosure, when the failure happened below the level the server could
/// describe — a timeout, a transport error, an unreadable body.
/// </param>
public sealed record CheckoutResult(
    CheckoutOutcome Outcome,
    OrderDocument? Order,
    CheckoutProblem? Problem,
    string? Detail)
{
    /// <summary>True when an order exists and the shopper should be taken to it.</summary>
    public bool Succeeded =>
        Outcome is CheckoutOutcome.Placed or CheckoutOutcome.Settling or CheckoutOutcome.AlreadyPlaced;

    /// <summary>
    /// Whether this attempt consumed the idempotency key, so a genuine retry needs a fresh one.
    /// <para>
    /// True for both 402s and for nothing else. A declined or abandoned payment leaves an order row
    /// bound to the key: sending it again would replay that dead order rather than attempt a new
    /// purchase, and the shopper would be stuck retrying their way back to the same refusal. Every
    /// other failure created no order at all — a 400, a 409 and a refused-before-anything-happened
    /// timeout all leave the key unspent — and reusing it is not merely allowed there but is the
    /// entire protection against a double submit.
    /// </para>
    /// </summary>
    public bool ConsumedTheKey => Outcome is CheckoutOutcome.Declined or CheckoutOutcome.NotCompleted;

    /// <summary>
    /// The order number the server named despite refusing, if any. Present on a 402 and on the
    /// interrupted case where the gateway could not be reached, and worth showing: an order the
    /// shopper cannot see is an order holding their stock in silence.
    /// </summary>
    public string? StrandedOrderNumber => Order is null ? Problem?.OrderNumber : null;
}
