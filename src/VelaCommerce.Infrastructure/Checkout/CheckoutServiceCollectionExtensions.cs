using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// Composition-root wiring for the checkout path.
/// </summary>
public static class CheckoutServiceCollectionExtensions
{
    /// <summary>
    /// Registers what <c>MapCheckoutEndpoints</c> resolves that nothing else already provides.
    /// <para>
    /// Today that is one thing: <see cref="TimeProvider"/>. ASP.NET Core does not register it, and
    /// checkout needs a clock — an order's <c>PlacedAt</c>, a reservation's expiry and the payment
    /// request's <c>RequestedAt</c> all have to be the <em>same</em> instant, and the architecture
    /// test forbids any type from reading <c>DateTimeOffset.UtcNow</c> to get it. Injecting the
    /// clock is what makes that rule satisfiable, and what lets a test place an order at an
    /// arbitrary moment and assert on when the reservation lapses without sleeping.
    /// </para>
    /// <para>
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, TService)"/>
    /// rather than <c>AddSingleton</c>, so a host that has already supplied its own
    /// <see cref="TimeProvider"/> — a test substituting a <c>FakeTimeProvider</c>, most obviously
    /// — keeps it. Calling this method twice is harmless.
    /// </para>
    /// <para>
    /// The other two things checkout depends on are registered by their own owners and are
    /// deliberately not duplicated here: <c>IPaymentGateway</c> comes from
    /// <c>AddPaymentSimulator</c>, and <c>IDataProtectionProvider</c> — which signs the
    /// order-retrieval links — comes from the <c>AddDataProtection</c> call the session cookie
    /// already needs. Registering them again from here would give the host two opinions about
    /// which gateway it is talking to.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCheckout(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Without the reaper, stock a checkout reserved but never paid for is held forever:
        // only a decline and a failure inside the reservation transaction hand units back on
        // their own, so one abandoned checkout of the last unit takes a product off sale
        // permanently. StockReservation.HasLapsed had no caller anywhere before this.
        services.AddHostedService<ReservationReaper>();

        return services;
    }
}
