using Microsoft.Extensions.Configuration;
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
    /// <para>
    /// It also starts <see cref="DemoDataPurge"/>, which is what gives a demo session an end. The
    /// two belong together: minting an identity per visitor and never expiring one is how a shop
    /// strangers share grows forever and slowly takes itself off sale.
    /// </para>
    /// </summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="configuration">
    /// Root configuration, for <c>Demo:Purge</c>. Optional so that a host with nothing to say about
    /// retention still calls this the short way — the defaults are the deployment's, and a missing
    /// section means demo data expires after a day.
    /// </param>
    public static IServiceCollection AddDemoSessionTenancy(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<DemoSession>();
        services.TryAddScoped<ICurrentDemoSession>(static provider => provider.GetRequiredService<DemoSession>());
        services.TryAddScoped<IDemoSessionBinder>(static provider => provider.GetRequiredService<DemoSession>());

        // Registered here and not in AddCheckout because this host may compose them in either
        // order, and the purge needs a clock as much as the checkout does.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(configuration is null
            ? new DemoDataPurgeOptions()
            : DemoDataPurgeOptions.FromConfiguration(configuration));

        // Without this, nothing in the system ever deletes a row. Carts, orders, price overlays and
        // outbox messages accumulate for the life of the deployment, and — the part that is not
        // merely about disk — an abandoned checkout in a state ReservationReaper is designed not to
        // touch holds its units on a stock ledger every visitor shares, permanently.
        services.AddHostedService<DemoDataPurge>();

        return services;
    }
}
