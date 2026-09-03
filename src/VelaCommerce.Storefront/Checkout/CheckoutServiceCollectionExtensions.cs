using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// Registration for the checkout slice.
/// </summary>
public static class CheckoutServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CheckoutState"/> so a checkout attempt outlives the page rendering it.
    ///
    /// <para>
    /// <strong>One line, in <c>src/VelaCommerce.Storefront/Program.cs</c>, beside the other
    /// <c>AddScoped</c> calls:</strong> <c>builder.Services.AddStorefrontCheckout();</c>
    /// </para>
    /// <para>
    /// Scoped, which in WebAssembly is one instance per tab — the lifetime a checkout attempt
    /// actually has. Idempotent: <see cref="ServiceCollectionDescriptorExtensions.TryAddScoped{TService}"/>
    /// means calling it twice, or calling it after someone has registered their own
    /// <see cref="CheckoutState"/>, changes nothing.
    /// </para>
    /// <para>
    /// <strong>Nothing else is required, and forgetting it does not break checkout.</strong> The two
    /// pages resolve <see cref="CheckoutState"/> through <see cref="IServiceProvider"/> and fall back
    /// to a page-local instance when it is absent, because a composition-root line nobody added must
    /// not be the reason a shopper cannot buy anything. The cost of the fallback is real but narrow:
    /// the address draft and the idempotency key stop surviving navigation away from the checkout
    /// page, so a shopper who leaves and returns starts a new attempt. Add the line.
    /// </para>
    /// <para>
    /// <see cref="CheckoutApiClient"/> is deliberately <em>not</em> registered. It is constructed by
    /// the pages from the single <see cref="HttpClient"/> already in the container, matching how
    /// <c>CartState</c> builds its own <c>CartApiClient</c>: it holds no state, and registering it
    /// would only add a second way for it to be wrong.
    /// </para>
    /// </summary>
    /// <param name="services">The storefront's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddStorefrontCheckout(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<CheckoutState>();

        return services;
    }
}
