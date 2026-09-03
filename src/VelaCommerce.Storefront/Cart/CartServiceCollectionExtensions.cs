using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using VelaCommerce.Storefront.Shell;

namespace VelaCommerce.Storefront.Cart;

/// <summary>
/// Registers the cart. Called once from the storefront's entry point.
/// </summary>
public static class CartServiceCollectionExtensions
{
    /// <summary>
    /// Adds the API-backed cart and the drawer's open/closed state.
    /// <para>
    /// <strong>Optional, and deliberately so.</strong> The cart resolves from the registrations the
    /// storefront already has — it depends on the app's <see cref="HttpClient"/>, the catalog and the
    /// shell, all of which are registered — so moving it to the API needed no change to the entry
    /// point at all. This method exists as the one line to write in place of the two
    /// <c>AddScoped&lt;…&gt;()</c> calls, and as the place a future dependency of the cart can be
    /// added without the entry point having to learn about it.
    /// </para>
    /// <para>
    /// Both registrations are scoped, which in WebAssembly means one instance for the life of the
    /// tab: one cart, one drawer. <c>TryAdd</c> makes the call idempotent, so leaving the existing
    /// lines in place alongside it is harmless.
    /// </para>
    /// <para>
    /// <see cref="CartApiClient"/> is deliberately not registered. It is a stateless adapter the cart
    /// builds over the app's own client, and registering it would give <see cref="CartState"/> a
    /// second viable constructor — an ambiguity the container resolves by throwing, at the moment a
    /// shopper first opens the drawer.
    /// </para>
    /// </summary>
    /// <param name="services">The storefront's service collection.</param>
    /// <returns>The same collection, so the call chains.</returns>
    public static IServiceCollection AddStorefrontCart(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<CartState>();
        services.TryAddScoped<CartDrawerState>();

        return services;
    }
}
