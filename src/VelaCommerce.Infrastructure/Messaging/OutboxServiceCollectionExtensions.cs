using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelaCommerce.Infrastructure.Messaging;

/// <summary>
/// Composition-root wiring for the transactional outbox.
/// </summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox dispatcher and everything it needs.
    /// <para>
    /// Nothing here is required to <em>enqueue</em> a message — that is a row added through the
    /// DbContext the endpoint already has, which is the entire point of an outbox: the promise
    /// costs one insert and depends on no service being alive. This registration is only about the
    /// half that delivers, so a host that calls <c>AddOutbox</c> and a host that does not write the
    /// same rows; only one of them drains the table.
    /// </para>
    /// <para>
    /// Configuration is read once, here, rather than through <c>IOptionsMonitor</c>. The receiver
    /// address is resolved from the host's own listening addresses at this moment, and a poll
    /// interval that changed under a running loop would take effect at an unpredictable point
    /// anyway — "restart to reconfigure" is the honest contract.
    /// </para>
    /// <para>
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>
    /// throughout, so a test that has already supplied its own <see cref="TimeProvider"/>, options
    /// or delivery client keeps them, and calling this twice is harmless. The hosted service is
    /// registered with <c>AddHostedService</c> and would therefore run twice if this were called
    /// twice — which is safe by construction here rather than by luck: two dispatchers cannot
    /// deliver one message, because each claims its rows with <c>FOR UPDATE SKIP LOCKED</c>.
    /// </para>
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">
    /// Root configuration. The <c>Messaging:Outbox</c> section is read from it, and so are the
    /// host's <c>urls</c> / <c>HTTP_PORTS</c> keys, which is how the dispatcher discovers where to
    /// post without anybody writing a port down. Every key is optional.
    /// </param>
    public static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = OutboxOptions.FromConfiguration(configuration);

        // Validated at registration, unlike the payment simulator's secret check. The difference is
        // what each one can fail on: that check depends on the environment, which the build-time
        // OpenAPI generator gets wrong (it runs this entry point as Production), so failing there
        // broke the build rather than a deployment. These are plain value checks — a negative
        // interval is wrong in every environment — and every default is valid, so a host with no
        // configuration cannot trip them.
        options.Validate();

        services.TryAddSingleton(options);

        // Registered here as well as in AddCheckout, because the dispatcher must work in a host
        // that composes the outbox without composing checkout. TryAdd keeps whichever came first.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(provider => new OutboxDeliveryClient(provider.GetRequiredService<OutboxOptions>()));

        services.AddHostedService<OutboxDispatcher>();

        return services;
    }
}
