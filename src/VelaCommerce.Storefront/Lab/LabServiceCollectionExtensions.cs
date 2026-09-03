using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelaCommerce.Storefront.Lab;

/// <summary>
/// Registration for the Demo Lab slice.
/// </summary>
public static class LabServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LabState"/> so transcripts and the published cooldown survive
    /// navigation away from the lab and back.
    ///
    /// <para>
    /// <strong>One line, in <c>src/VelaCommerce.Storefront/Program.cs</c>, beside the other
    /// <c>AddScoped</c> calls:</strong> <c>builder.Services.AddStorefrontLab();</c>
    /// </para>
    /// <para>
    /// Scoped, which in WebAssembly is one instance per tab — the lifetime a reading session
    /// actually has. Idempotent: <see cref="ServiceCollectionDescriptorExtensions.TryAddScoped{TService}"/>
    /// means calling it twice changes nothing.
    /// </para>
    /// <para>
    /// <strong>Nothing else is required, and forgetting it does not break the lab.</strong>
    /// <c>Lab.razor</c> resolves <see cref="LabState"/> through <see cref="IServiceProvider"/> and
    /// falls back to a page-local instance when it is absent, for the same reason the checkout page
    /// does: a composition-root line nobody added must not be the reason a page throws. The cost of
    /// the fallback is narrow and real — leaving the lab and coming back refetches the catalogue and
    /// discards the transcripts already produced, and the cooldown countdown restarts unaware, so a
    /// reviewer can walk into a 429 the page would otherwise have predicted. Add the line.
    /// </para>
    /// <para>
    /// <see cref="LabApiClient"/> is deliberately <em>not</em> registered, matching
    /// <c>CheckoutApiClient</c>: it holds no state, the page builds it from the single
    /// <see cref="HttpClient"/> already in the container, and registering it would only add a second
    /// way for it to be wrong.
    /// </para>
    /// </summary>
    /// <param name="services">The storefront's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddStorefrontLab(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<LabState>();

        return services;
    }
}
