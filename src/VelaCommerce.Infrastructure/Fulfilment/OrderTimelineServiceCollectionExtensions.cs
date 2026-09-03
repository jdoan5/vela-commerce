using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelaCommerce.Infrastructure.Fulfilment;

/// <summary>
/// Composition-root wiring for the accelerated order timeline.
/// </summary>
public static class OrderTimelineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the worker that walks paid orders through Packed and Shipped.
    /// <para>
    /// Nothing else in the solution needs to be told this is running: the worker takes its work
    /// from <c>orders.status</c> and <c>orders.paid_at</c>, both of which are written by checkout
    /// and by the settlement path whether or not anybody is advancing anything. A host that omits
    /// this call has a working shop whose orders simply stop at Paid, which is the correct shape
    /// for the "off" case — a demo narrated by hand, or a replica that should not double-drive the
    /// timeline (see <see cref="OrderTimelineOptions.Enabled"/>).
    /// </para>
    /// <para>
    /// Configuration is read once, here, rather than through <c>IOptionsMonitor</c>. A dwell time
    /// that changed under a running loop would take effect at an unpredictable point — orders
    /// already past the new deadline would all become due at once — so "restart to reconfigure" is
    /// both the honest contract and the legible one.
    /// </para>
    /// <para>
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, TService)"/>
    /// for the clock and the options, so a test that has already supplied a <c>FakeTimeProvider</c>
    /// or its own options keeps them. Calling this method twice is harmless end to end:
    /// <c>AddHostedService</c> registers through <c>TryAddEnumerable</c>, so the second call adds
    /// no second worker (verified — two calls, one <c>IHostedService</c>). Two <em>processes</em>
    /// running it is harmless too, and by construction rather than by luck: each claims its orders
    /// with <c>FOR UPDATE SKIP LOCKED</c>, and the state machine refuses a transition the other one
    /// already made.
    /// </para>
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">
    /// Root configuration; the <c>Fulfilment:Timeline</c> section is read from it. Every key is
    /// optional, so a fresh clone gets the demo defaults — Paid to Packed in 20 seconds, Packed to
    /// Shipped 40 seconds after that — with no configuration file at all.
    /// </param>
    public static IServiceCollection AddOrderTimeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = OrderTimelineOptions.FromConfiguration(configuration);

        // Validated at registration. Every check is a plain value check that every default passes,
        // so a host with no configuration cannot trip it — which matters because the build-time
        // OpenAPI generator executes the composition root, and a registration that can throw there
        // breaks the build rather than a deployment.
        options.Validate();

        services.TryAddSingleton(options);

        // Registered here as well as in AddCheckout and AddOutbox, because the worker must compose
        // in a host that wants the timeline without those. TryAdd keeps whichever came first, so
        // every one of them agrees on a single clock.
        services.TryAddSingleton(TimeProvider.System);

        services.AddHostedService<OrderTimelineWorker>();

        return services;
    }
}
