namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// What a checkout attempt is, held somewhere that outlives the page rendering it: the address being
/// typed, the simulator path chosen, the idempotency key, and the receipt handed from the checkout
/// to the order page.
///
/// <para>
/// <strong>The idempotency key is the reason this class exists.</strong> A key generated in a
/// component field is a key that is regenerated whenever the component is. Blazor tears a page down
/// on navigation, so a shopper who bounces to the cart drawer, thinks again, and comes back would
/// arrive holding a different key — and if their first submit had in fact reached the server and
/// only the response was lost, the second submit would create a second order and take a second
/// payment. That is precisely the failure the key exists to prevent, reintroduced by where the key
/// was stored. So it lives here, in a scoped service, which in WebAssembly means one instance for
/// the life of the tab.
/// </para>
/// <para>
/// <strong>What it deliberately does not do is survive a reload.</strong> Persisting the key would
/// mean a second source of truth in browser storage, which this storefront removed from the cart on
/// purpose. The gap that leaves is smaller than it looks: a checkout that actually succeeded emptied
/// the server's cart as part of the same transaction, so a reloaded shopper with a fresh key reaches
/// a checkout page with nothing in it to buy, and is told so rather than charged again. The server's
/// state, not the client's memory, is what closes that hole.
/// </para>
/// </summary>
public sealed class CheckoutState
{
    private string? _idempotencyKey;
    private OrderDocument? _receipt;

    /// <summary>
    /// The address being typed. One instance for the tab, so a shopper who wanders off to look at
    /// the cart and comes back finds their address where they left it.
    /// </summary>
    public AddressDraft Address { get; } = new();

    /// <summary>Which path the payment simulator should take. Always one of <see cref="PaymentScenarios.All"/>.</summary>
    public string PaymentScenario { get; set; } = PaymentScenarios.DefaultHint;

    /// <summary>
    /// This attempt's key, minted on first use and stable until something spends it.
    /// <para>
    /// Every retry of a failed submit sends this same string, which is the entire point: the server
    /// answers the second send by handing back the order the first one created rather than by
    /// creating another. It is only replaced by <see cref="StartNewAttempt"/>.
    /// </para>
    /// </summary>
    public string IdempotencyKey => _idempotencyKey ??= NewKey();

    /// <summary>
    /// Abandons the current key and starts a fresh attempt.
    /// <para>
    /// Called in exactly one situation, and calling it anywhere else would be a bug: the server has
    /// answered 402, which means an order already belongs to this key and it is either cancelled or
    /// pending-and-unpaid. Sending the key again would replay that dead order forever. Every other
    /// failure — a 400, either 409, a timeout, a 502 — created no order, so the key is unspent and
    /// reusing it is the protection rather than the risk.
    /// </para>
    /// </summary>
    public void StartNewAttempt() => _idempotencyKey = NewKey();

    /// <summary>
    /// Hands the freshly placed order to the order page.
    /// <para>
    /// Not an optimisation for its own sake. The checkout response is the only place the gateway's
    /// answer ever appears — <c>payment.outcome</c>, the gateway reference, whether settlement is
    /// still coming — because none of it is persisted for a later GET to find. Without this handoff
    /// the confirmation screen would have to either drop that answer or re-ask a question the API
    /// cannot answer twice.
    /// </para>
    /// </summary>
    public void RememberReceipt(OrderDocument order) => _receipt = order;

    /// <summary>
    /// Takes the remembered receipt for an order number, once.
    /// <para>
    /// Once, because it is a snapshot of one instant. The order page polls from here on, and handing
    /// the same frozen document back on a later visit would let a cancelled order keep showing the
    /// authorisation that preceded its cancellation.
    /// </para>
    /// </summary>
    /// <param name="orderNumber">The order the page is about to render. A receipt for anything else is ignored and kept.</param>
    public OrderDocument? TakeReceipt(string? orderNumber)
    {
        if (_receipt is null || !string.Equals(_receipt.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var receipt = _receipt;
        _receipt = null;

        return receipt;
    }

    /// <summary>
    /// A key that is unique to this visitor's attempt.
    /// <para>
    /// A GUID rather than something derived from the cart's contents. A content hash sounds tidier
    /// and is wrong: two genuinely separate purchases of the same basket would collide, and the
    /// second would be answered with the first one's order. The key identifies an <em>attempt</em>,
    /// not a basket. Thirty-two characters, comfortably inside the API's 128-character limit and
    /// with no control characters for it to reject.
    /// </para>
    /// </summary>
    private static string NewKey() => Guid.NewGuid().ToString("N");
}
