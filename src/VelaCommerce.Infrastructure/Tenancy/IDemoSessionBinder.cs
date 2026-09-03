namespace VelaCommerce.Infrastructure.Tenancy;

/// <summary>
/// The write side of <see cref="ICurrentDemoSession"/>, split out so that being able to
/// <em>read</em> the current session does not imply being able to <em>change</em> it.
/// <para>
/// Everything that queries data takes <see cref="ICurrentDemoSession"/> and can only ask. Exactly
/// one component — the cookie middleware at the edge of the host — takes this interface, and it
/// runs before any endpoint. A service that later decided to "just switch tenants for a moment"
/// would have to inject a second, obviously-named dependency to do it, which is the kind of change
/// that gets noticed in review rather than the kind that hides behind a property setter.
/// </para>
/// </summary>
public interface IDemoSessionBinder
{
    /// <summary>
    /// Binds the current request to a visitor, once.
    /// <para>
    /// A second call throws instead of overwriting: within one request the identity is decided at
    /// the edge and then frozen, so no downstream code can re-point an in-flight unit of work at
    /// another visitor's rows. <see cref="Guid.Empty"/> is rejected for the same reason — a
    /// sentinel that looks like a real id is how "no session" quietly turns into "some session".
    /// </para>
    /// </summary>
    /// <param name="sessionId">The visitor's demo session id. Must not be <see cref="Guid.Empty"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="InvalidOperationException">A session is already bound to this scope.</exception>
    void Bind(Guid sessionId);
}
