namespace VelaCommerce.Storefront.Shell;

/// <summary>
/// Whether the cart drawer is open, and nothing else.
/// <para>
/// The button that opens the drawer lives in the header and the drawer lives in the layout,
/// so the two cannot hold this between them. It is deliberately separate from
/// <see cref="CartState"/>: what is in the cart is shop state that survives a reload,
/// whether a panel is open is not.
/// </para>
/// <para>
/// <see cref="Closed"/> exists for one reason: a dialog must hand focus back to whatever
/// opened it. The header subscribes to it and re-focuses its own button, which is the only
/// thing that can open the drawer.
/// </para>
/// </summary>
public sealed class CartDrawerState
{
    private bool _isOpen;

    /// <summary>Raised whenever the drawer opens or closes.</summary>
    public event Action? Changed;

    /// <summary>Raised after the drawer closes, so the trigger can take focus back.</summary>
    public event Action? Closed;

    /// <summary>True while the drawer is on screen and holding focus.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>Opens the drawer. A no-op when it is already open.</summary>
    public void Open()
    {
        if (_isOpen)
            return;

        _isOpen = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes the drawer and, by default, asks the trigger to take focus back. A no-op when
    /// it is already closed, so an Escape press on a closed drawer cannot steal focus.
    /// </summary>
    /// <param name="restoreFocus">
    /// Pass false when the close accompanies a navigation. Blazor's <c>FocusOnNavigate</c>
    /// moves focus to the destination heading, and restoring it to the trigger would race
    /// that and win, stranding a keyboard user on the page they just left.
    /// </param>
    public void Close(bool restoreFocus = true)
    {
        if (!_isOpen)
            return;

        _isOpen = false;
        Changed?.Invoke();

        if (restoreFocus)
            Closed?.Invoke();
    }

    /// <summary>Opens the drawer if it is closed, closes it if it is open.</summary>
    public void Toggle()
    {
        if (_isOpen)
            Close();
        else
            Open();
    }
}
