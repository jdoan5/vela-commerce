namespace VelaCommerce.Infrastructure.Tenancy;

/// <summary>
/// The per-request holder behind <see cref="ICurrentDemoSession"/> and
/// <see cref="IDemoSessionBinder"/>.
/// <para>
/// Registered scoped and resolved through both interfaces, so the middleware writing the value and
/// the DbContext reading it are looking at the same object for the duration of one request. The
/// class itself is internal: outside the composition root there is no way to name it, and therefore
/// no way to construct a detached one, hand it a session id and quietly use it as an ambient
/// override.
/// </para>
/// <para>
/// It starts unbound on purpose. A scope that never runs the middleware — a hosted service, a
/// migration, a test that forgot — keeps <see cref="SessionId"/> null and is filtered down to
/// nothing rather than up to everything.
/// </para>
/// </summary>
internal sealed class DemoSession : ICurrentDemoSession, IDemoSessionBinder
{
    public Guid? SessionId { get; private set; }

    public void Bind(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty GUID is not a demo session. Treat an unreadable or missing cookie as "
                + "'no session' and issue a fresh id instead of binding a placeholder.",
                nameof(sessionId));
        }

        if (SessionId is not null)
        {
            throw new InvalidOperationException(
                $"This scope is already bound to demo session {SessionId}. A request identifies "
                + "one visitor, decided once at the edge; rebinding mid-request would let work "
                + "started for one visitor finish against another's rows.");
        }

        SessionId = sessionId;
    }
}
