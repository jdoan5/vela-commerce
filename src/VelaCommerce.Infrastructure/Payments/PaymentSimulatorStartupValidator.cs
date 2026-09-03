using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// Refuses to serve payments with the repository's public development secret.
/// <para>
/// The check runs here, when the host starts, rather than inside
/// <c>AddPaymentSimulator</c>, because registration also happens under the build-time OpenAPI
/// generator. That generator executes the entry point with no environment set, so it looks
/// like Production and tripped the guard — turning a deployment safeguard into a broken
/// build. The generator does start hosted services, so this alone was not enough either: the
/// hard refusal now lives on the paths that take money
/// (<see cref="PaymentSimulatorOptions.AssertUsable"/>), and this service logs the same
/// problem loudly at startup so a misconfigured deployment is obvious before a shopper finds
/// it rather than after.
/// </para>
/// </summary>
internal sealed class PaymentSimulatorStartupValidator(
    PaymentSimulatorOptions options,
    bool isDevelopment,
    ILogger<PaymentSimulatorStartupValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Structural problems are still fatal: a short or empty key, or a settlement delay
        // longer than the signature window, means nothing will ever verify.
        options.Validate(isDevelopment);

        if (!isDevelopment && options.UsesDevelopmentSecret)
        {
            logger.LogCritical(
                "The payment simulator is running outside Development with the signing secret that "
                + "is committed to this repository. Anyone could forge a settlement notification. "
                + "Payments will be refused until {Section}:SigningSecret is set to a real value.",
                PaymentSimulatorOptions.SectionName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
