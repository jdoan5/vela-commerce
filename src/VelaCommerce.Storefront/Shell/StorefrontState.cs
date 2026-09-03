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
/// This is a seam, not a cart. When the cart feature lands it should own its own state and
/// call <see cref="SetCartItemCount"/> so the badge stays a dumb readout.
/// </para>
/// </summary>
public sealed class StorefrontState
{
    private string _searchTerm = string.Empty;
    private int _cartItemCount;

    /// <summary>Raised after any property changes. Subscribers must unsubscribe on dispose.</summary>
    public event Action? Changed;

    /// <summary>What is currently typed in the header's search field. Never null.</summary>
    public string SearchTerm => _searchTerm;

    /// <summary>True when the search field has something in it worth filtering by.</summary>
    public bool HasSearch => !string.IsNullOrWhiteSpace(_searchTerm);

    /// <summary>How many line items the badge should show.</summary>
    public int CartItemCount => _cartItemCount;

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

    /// <summary>Sets the cart badge count. Negative values are clamped rather than trusted.</summary>
    public void SetCartItemCount(int count)
    {
        var next = Math.Max(0, count);
        if (next == _cartItemCount)
            return;

        _cartItemCount = next;
        Changed?.Invoke();
    }
}
