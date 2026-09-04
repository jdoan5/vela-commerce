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

        if (!isDevelopment && options.UsesPubliclyKnownSecret)
        {
            // Publicly-known covers two values, not one: the committed development default and the
            // placeholder Terraform applies with. The second is the one a real deployment actually
            // hits, because the design deliberately applies with it and expects the operator to
            // replace it out-of-band — so the skipped step, not the forgotten override, is the
            // likely way a public key ends up signing real notifications.
            logger.LogCritical(
                "The payment simulator is running outside Development with a signing secret that is "
                + "published in this repository ({Which}). Anyone could forge a settlement "
                + "notification. Payments will be refused until {Section}:SigningSecret is set to a "
                + "real value.",
                options.UsesDevelopmentSecret ? "the committed development default" : "the Terraform placeholder",
                PaymentSimulatorOptions.SectionName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
