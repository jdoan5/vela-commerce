namespace VelaCommerce.Infrastructure.Tenancy;

/// <summary>
/// Who is asking, on a site with no accounts.
/// <para>
/// The live demo is one deployment shared by strangers, so "whose cart is this" has to be
/// answered by something, and that something is a signed cookie rather than a login. This
/// interface is the read side of that answer: the persistence layer consults it to decide which
/// rows exist for the current request, and nothing else in the application needs to care how the
/// value arrived.
/// </para>
/// <para>
/// <see cref="SessionId"/> is deliberately nullable, and null is the state to design around: a
/// background job, a design-time tool, a unit test and a request that arrived before the session
/// middleware ran all see null. The query filter treats null as "match nothing", so every one of
/// those callers sees an empty cart list instead of everyone's. An empty screen is a bug report;
/// a stranger's order history is an incident.
/// </para>
/// </summary>
public interface ICurrentDemoSession
{
    /// <summary>
    /// The visitor's demo session, or <see langword="null"/> when no session has been established
    /// for this scope. Read late — at query time, not at construction — because the middleware
    /// binds it after the DI scope is created.
    /// </summary>
    Guid? SessionId { get; }
}
