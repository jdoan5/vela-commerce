using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VelaCommerce.Domain.Payments;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// Composition-root wiring for the in-repository payment gateway.
/// </summary>
public static class PaymentSimulatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the simulator as the application's <see cref="IPaymentGateway"/>.
    /// <para>
    /// Environment is inferred from configuration and defaults to Production when nothing says
    /// otherwise — the same default the ASP.NET Core host itself uses — so the committed
    /// development signing secret cannot reach a real deployment merely because an environment
    /// variable was forgotten. Use the
    /// <see cref="AddPaymentSimulator(IServiceCollection, IConfiguration, bool)"/> overload where
    /// the host already knows its own <c>IHostEnvironment</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPaymentSimulator(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddPaymentSimulator(configuration, IsDevelopment(configuration));
    }

    /// <summary>
    /// Registers the simulator, with the host stating whether this is Development.
    /// <para>
    /// One instance behind three registrations, the same shape the demo-session wiring uses: the
    /// domain talks to <see cref="IPaymentGateway"/> and knows nothing else, the checkout handler
    /// and the outbox worker take <see cref="IPaymentSimulator"/> for the settlement plan, and
    /// tests can resolve the concrete type. Splitting these across separate instances would give
    /// the two halves different options objects the first time someone reconfigured one of them.
    /// </para>
    /// <para>
    /// Singleton because the gateway holds only immutable configuration and does no I/O. That is
    /// also why configuration is read once, here, rather than through <c>IOptionsMonitor</c>: a
    /// signing secret that changed under a running process would invalidate notifications already
    /// in flight, so "restart to rotate" is the honest contract, not a limitation.
    /// </para>
    /// <para>
    /// <b>Production checklist.</b> Set
    /// <c>Payments:Simulator:SigningSecret</c> from an environment variable or a key vault
    /// reference — never from <c>appsettings.Production.json</c>, which ships inside the container
    /// image. <see cref="PaymentSimulatorOptions.AssertUsable"/> then refuses to authorize a payment
    /// or verify a settlement outside Development while a publicly-known secret is in place, and
    /// <c>PaymentSimulatorStartupValidator</c> logs it at Critical on boot.
    /// <para>
    /// <b>The host still starts.</b> The refusal is on the money paths, not at startup, because
    /// startup also happens under the build-time OpenAPI generator — which runs this entry point
    /// with no environment set and therefore looks like Production, so refusing to boot there broke
    /// the build rather than a deployment. The practical consequence is worth knowing: a deployment
    /// that forgets the secret serves the shop, passes a health check and fails only when somebody
    /// tries to buy something.
    /// </para>
    /// </para>
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">
    /// Root configuration; the <c>Payments:Simulator</c> section is read from it. Every key is
    /// optional, so a host with no configuration at all still gets a working gateway.
    /// </param>
    /// <param name="isDevelopment">Whether the host is running in the Development environment.</param>
    public static IServiceCollection AddPaymentSimulator(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = PaymentSimulatorOptions.FromConfiguration(
            configuration.GetSection(PaymentSimulatorOptions.SectionName));

        services.TryAddSingleton(options);

        // Validate when the host STARTS, not when services are registered. Failing fast still
        // matters — a bad secret surfaces only as a signature that will not verify, which in a
        // log is indistinguishable from an attack. But registration also happens during
        // build-time OpenAPI generation, which runs this entry point with no environment set
        // and therefore looks like Production. Throwing there broke the build rather than the
        // deployment. A hosted service runs on real startup and not during that generation.
        services.AddHostedService(provider => new PaymentSimulatorStartupValidator(
            provider.GetRequiredService<PaymentSimulatorOptions>(),
            isDevelopment,
            provider.GetRequiredService<ILogger<PaymentSimulatorStartupValidator>>()));
        services.TryAddSingleton(provider => new SimulatedPaymentGateway(
            provider.GetRequiredService<PaymentSimulatorOptions>(),
            provider.GetRequiredService<ILogger<SimulatedPaymentGateway>>(),
            isDevelopment));
        services.TryAddSingleton<IPaymentGateway>(static provider => provider.GetRequiredService<SimulatedPaymentGateway>());
        services.TryAddSingleton<IPaymentSimulator>(static provider => provider.GetRequiredService<SimulatedPaymentGateway>());

        return services;
    }

    /// <summary>
    /// Reads the environment the way the host does, without taking a dependency on
    /// <c>Microsoft.Extensions.Hosting.Abstractions</c> for one string comparison. Absent means
    /// Production, which is the host's own default and the safe direction to guess in.
    /// </summary>
    private static bool IsDevelopment(IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"]
                          ?? configuration["DOTNET_ENVIRONMENT"]
                          ?? "Production";

        return string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
