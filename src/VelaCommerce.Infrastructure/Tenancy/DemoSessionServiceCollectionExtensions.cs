using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelaCommerce.Infrastructure.Tenancy;

/// <summary>
/// Composition-root wiring for per-visitor isolation.
/// </summary>
public static class DemoSessionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the read and write halves of the current demo session as one scoped object.
    /// <para>
    /// Both interfaces resolve to the same <c>DemoSession</c> instance for the lifetime of a
    /// request, which is what lets the middleware bind a value that a DbContext created earlier in
    /// the same scope will still see: the context captures the accessor, not the id, and reads it
    /// when a query is actually translated.
    /// </para>
    /// <para>
    /// Forgetting this call does not disable tenancy. The DbContext treats an absent accessor
    /// exactly like an unbound one — no session, therefore no rows — so the failure mode of a
    /// missing registration is an empty cart, not a shared one.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDemoSessionTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<DemoSession>();
        services.TryAddScoped<ICurrentDemoSession>(static provider => provider.GetRequiredService<DemoSession>());
        services.TryAddScoped<IDemoSessionBinder>(static provider => provider.GetRequiredService<DemoSession>());

        return services;
    }
}
