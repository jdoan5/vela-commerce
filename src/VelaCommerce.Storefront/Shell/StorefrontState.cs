namespace VelaCommerce.Storefront.Shell;

/// <summary>
/// The small amount of state the app shell and the pages have to agree on.
/// <para>
/// The search field lives in the header but filters the catalog page, and the cart badge
/// lives in the header but is written by whatever owns the cart. Rather than thread
/// callbacks through the layout, both sides talk to this one object and re-render on
/// <see cref="Changed"/>. It is registered scoped, which in WebAssembly means one instance
/// for the life of the tab.
/// </para>
/// <para>
/// This is a seam, not a cart. <see cref="CartState"/> owns the cart and calls
/// <see cref="SetCartItemCount"/>, so the badge stays a dumb readout of a number somebody else
/// is responsible for.
/// </para>
/// </summary>
public sealed class StorefrontState
{
    private string _searchTerm = string.Empty;
    private int _cartItemCount;
    private bool _cartItemCountIsConfirmed;

    /// <summary>Raised after any property changes. Subscribers must unsubscribe on dispose.</summary>
    public event Action? Changed;

    /// <summary>What is currently typed in the header's search field. Never null.</summary>
    public string SearchTerm => _searchTerm;

    /// <summary>True when the search field has something in it worth filtering by.</summary>
    public bool HasSearch => !string.IsNullOrWhiteSpace(_searchTerm);

    /// <summary>How many units the badge should show.</summary>
    public int CartItemCount => _cartItemCount;

    /// <summary>
    /// Whether <see cref="CartItemCount"/> is a number the server confirmed, rather than the zero a
    /// page starts on.
    /// <para>
    /// The cart lives on the server now and is not fetched until it is first needed, so before then
    /// "0" means "we have not asked", not "the cart is empty" — and a returning visitor's cart
    /// genuinely may not be empty. The distinction is published rather than hidden behind a zero so
    /// the header can render an unconfirmed badge differently instead of asserting something it does
    /// not know. Deliberately not solved by fetching the cart on first paint: that would wake a
    /// sleeping API for every visitor who only wanted to browse, which is the one thing this
    /// storefront is built not to do.
    /// </para>
    /// </summary>
    public bool CartItemCountIsConfirmed => _cartItemCountIsConfirmed;

    /// <summary>Sets the search term. A no-op when the value has not actually changed, so a keystroke that produces the same string does not re-render the grid.</summary>
    public void SetSearchTerm(string? value)
    {
        var next = value ?? string.Empty;
        if (string.Equals(next, _searchTerm, StringComparison.Ordinal))
            return;

        _searchTerm = next;
        Changed?.Invoke();
    }

    /// <summary>Clears the search field.</summary>
    public void ClearSearch() => SetSearchTerm(string.Empty);

    /// <summary>
    /// Sets the cart badge count. Negative values are clamped rather than trusted.
    /// </summary>
    /// <param name="count">Units across every line.</param>
    /// <param name="confirmed">
    /// True when this number came back from the server, false when it is the client's optimistic
    /// guess between a click and the response that settles it. Once true it stays true: a later
    /// optimistic update is a guess about a cart the server has already described, which is a very
    /// different thing from never having asked.
    /// </param>
    public void SetCartItemCount(int count, bool confirmed = false)
    {
        var next = Math.Max(0, count);
        var nextConfirmed = _cartItemCountIsConfirmed || confirmed;

        if (next == _cartItemCount && nextConfirmed == _cartItemCountIsConfirmed)
            return;

        _cartItemCount = next;
        _cartItemCountIsConfirmed = nextConfirmed;
        Changed?.Invoke();
    }
}
