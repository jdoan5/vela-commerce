namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// The handful of durations and limits checkout runs on, in one place so the endpoint reads as a
/// sequence of decisions rather than as a scattering of magic numbers.
/// <para>
/// Constants rather than bound configuration, and that is a considered choice rather than a
/// shortcut: every value here changes the meaning of a persisted row — how long stock stays
/// promised, how long a receipt link opens — so changing one is a deployment with a migration
/// story, not a knob to turn at runtime. The payment simulator's options are bound from
/// configuration because a signing secret genuinely differs per environment; none of these do.
/// </para>
/// </summary>
public static class CheckoutPolicy
{
    /// <summary>
    /// How long a reservation holds stock before the reaper may release it.
    /// <para>
    /// This is the window between "checkout started" and "payment settled", so it has to outlast
    /// the slowest settlement the demo can produce (the simulator's deferred scenarios settle in
    /// seconds) while still being short enough that an abandoned checkout does not hold the last
    /// unit all afternoon. Fifteen minutes is the figure most real carts use, for the same reason.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ReservationWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a signed order-retrieval link stays valid.
    /// <para>
    /// Long enough to be a receipt somebody can come back to, short enough that a link forwarded
    /// on or left in a browser history stops working eventually. The lifetime is baked into the
    /// protected payload, so it is enforced by the server on every use rather than by hoping the
    /// holder discards the link.
    /// </para>
    /// </summary>
    public static readonly TimeSpan RetrievalLinkLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Matches <c>orders.idempotency_key</c>, which is <c>varchar(128)</c>. Checked in the handler
    /// so an over-long key is a 400 that names the limit, rather than a 500 from PostgreSQL
    /// refusing the insert after stock has already been reserved.
    /// </summary>
    public const int MaxIdempotencyKeyLength = 128;
}
