using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelaCommerce.Infrastructure.DemoLab;

/// <summary>
/// Composes the Demo Lab's supporting services.
/// </summary>
public static class DemoLabServiceCollectionExtensions
{
    /// <summary>
    /// Registers the lab's bounds, its admission control and its loopback client. Call it from the
    /// composition root as <c>builder.Services.AddDemoLab(builder.Configuration);</c>, before
    /// <c>MapDemoLabEndpoints()</c>.
    /// <para>
    /// <b>The endpoints degrade rather than fail if this is never called.</b>
    /// <c>DemoLabEndpoints</c> resolves these three optionally and answers 503 with a message
    /// naming this method, because the alternative - a required constructor dependency - turns a
    /// wiring mistake into a 500 with a stack trace, and because the build-time OpenAPI generator
    /// composes the real entry point and must never be the thing that discovers a missing
    /// registration.
    /// </para>
    /// <para>
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>
    /// throughout, following <c>AddOutbox</c>: calling this twice is harmless, and a test that has
    /// already supplied its own <see cref="TimeProvider"/> or options keeps them. Both singletons
    /// are disposable and are disposed by the container with the host.
    /// </para>
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">
    /// Root configuration; the <c>Demo:Lab</c> section is read from it. Every key is optional.
    /// </param>
    public static IServiceCollection AddDemoLab(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = DemoLabOptions.FromConfiguration(configuration);

        // Validated here, like the outbox's options and unlike the payment simulator's environment
        // check: these are plain value comparisons that mean the same thing in every environment,
        // so they cannot fail only under the build-time generator's Production composition.
        options.Validate();

        services.TryAddSingleton(options);

        // Also registered by AddCheckout; the lab needs it in its own right for the cooldown clock,
        // and must work in a host that composes the lab without composing checkout.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<DemoLabThrottle>();
        services.TryAddSingleton(provider => new DemoLabLoopback(provider.GetRequiredService<DemoLabOptions>()));

        return services;
    }
}
