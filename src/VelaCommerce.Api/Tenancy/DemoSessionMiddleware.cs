using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Tenancy;

/// <summary>
/// Gives every visitor to the shared demo an identity, without asking them for one.
/// <para>
/// There is no login here, so the only thing distinguishing two people browsing at the same time
/// is a cookie. That makes the cookie a credential, and an unsigned one would be a formality: a
/// visitor could type someone else's session id into their browser and inherit their cart and
/// order history. The value is therefore encrypted and authenticated with ASP.NET Core Data
/// Protection, which means a forged or edited cookie fails to decrypt and is discarded rather than
/// believed.
/// </para>
/// <para>
/// The middleware runs for every request, including the health probes and the OpenAPI document.
/// Issuing a cookie to a load balancer is a few wasted bytes and no server-side state at all —
/// nothing is stored for a session, the cookie <em>is</em> the session — which is cheaper than
/// teaching this class which paths are "real".
/// </para>
/// </summary>
public sealed class DemoSessionMiddleware
{
    /// <summary>Named so that anyone looking at their browser's storage can tell what it is and who set it.</summary>
    /// <summary>
    /// The session cookie's name.
    /// <para>
    /// DEPLOY-PHASE TODO: rename to <c>__Host-vela.session</c> once the API is served over
    /// HTTPS on a settled domain. The prefix makes a browser reject the cookie unless it is
    /// Secure, host-only and Path=/, which is what stops a sibling subdomain setting a second
    /// <c>vela.session</c> that shadows this one and fixes a visitor onto an attacker's
    /// session. It cannot be adopted now: the prefix requires Secure, Development runs over
    /// plain HTTP, and the storefront/API domain split is not decided yet — that decision is
    /// the same one the SameSite note below depends on, so both move together.
    /// </para>
    /// </summary>
    public const string CookieName = "vela.session";

    /// <summary>
    /// The Data Protection purpose. Purposes isolate ciphertexts: a payload protected for this
    /// string cannot be unprotected by any other component of the app, so a token minted for some
    /// future feature can never be replayed as a session id. The <c>.v1</c> is the upgrade seam —
    /// changing the payload format means bumping it, which invalidates old cookies by construction
    /// instead of by hoping every parser stays backwards-compatible.
    /// </summary>
    private const string ProtectorPurpose = "VelaCommerce.DemoSession.v1";

    /// <summary>
    /// Long enough that a visitor who comes back tomorrow still has their cart, short enough that
    /// a cookie copied off a shared machine stops working. Enforced twice, and the two are not
    /// equivalent: the cookie's Max-Age is a request the browser may ignore, while the same span
    /// baked into the protected payload is checked server-side on every read.
    /// </summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    private readonly RequestDelegate _next;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<DemoSessionMiddleware> _logger;
    private readonly bool _requireSecureCookie;

    public DemoSessionMiddleware(
        RequestDelegate next,
        IDataProtectionProvider dataProtection,
        IHostEnvironment environment,
        ILogger<DemoSessionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(environment);

        _next = next;
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _logger = logger;

        // Secure everywhere the demo actually runs; relaxed only for http://localhost, where the
        // browser would otherwise drop the cookie and every request would look like a new visitor.
        _requireSecureCookie = !environment.IsDevelopment();
    }

    /// <summary>
    /// Establishes the session for this request before anything downstream can query.
    /// <para>
    /// The binder is taken as a parameter rather than a constructor dependency because the
    /// middleware instance is a singleton and the session is scoped; ASP.NET Core resolves this
    /// argument from the request's own scope, which is the same scope the DbContext will be
    /// resolved from.
    /// </para>
    /// </summary>
    public async Task InvokeAsync(HttpContext context, IDemoSessionBinder binder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binder);

        var sessionId = ReadSessionCookie(context) ?? IssueSessionCookie(context);

        binder.Bind(sessionId);

        await _next(context);
    }

    /// <summary>
    /// Reads and verifies the cookie, or reports that there isn't a usable one.
    /// <para>
    /// Every failure — absent, truncated, edited, encrypted under a key that no longer exists,
    /// past its embedded expiry — lands in the same place: <see langword="null"/>, and the caller
    /// mints a fresh session. This method never throws and never trusts the raw string, because
    /// the raw string is the one input on the request that an attacker fully controls. A tampered
    /// cookie is not an error worth a 500; it is simply not a session.
    /// </para>
    /// </summary>
    private Guid? ReadSessionCookie(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie) || string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        try
        {
            var payload = _protector.Unprotect(cookie);

            // Guid.Empty is rejected here as well as in the binder. It cannot arrive from a forged
            // cookie — that would not decrypt — but it could arrive from a genuine one minted by a
            // future bug, and the whole point of the fail-closed filter is that no id is allowed to
            // mean "everyone".
            return Guid.TryParseExact(payload, "N", out var sessionId) && sessionId != Guid.Empty
                ? sessionId
                : null;
        }
        catch (CryptographicException)
        {
            // Deliberately logs the fact and not the value: the cookie is attacker-controlled text
            // and a credential, so it belongs in neither the log file nor the response.
            _logger.LogDebug(
                "Discarded an unreadable {CookieName} cookie and issued a new demo session.",
                CookieName);

            return null;
        }
    }

    /// <summary>Mints a new session and sets the cookie on the way out.</summary>
    private Guid IssueSessionCookie(HttpContext context)
    {
        // UUIDv7, like every other identifier in this system: time-ordered, so the rows a session
        // creates cluster in the index. Its 74 random bits are not what protects it — the id
        // never leaves the server in the clear, and the cookie is sealed with Data Protection,
        // so guessing an id buys nothing without the key.
        var sessionId = Guid.CreateVersion7();

        // This response now carries a credential, so it must never be stored by a shared cache.
        // The cart group already says no-store for itself, but the cookie is minted on whatever
        // request arrives first — usually a catalog read, which is exactly the kind of response a
        // CDN would happily keep. Saying it here covers every endpoint, including future ones.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Append("Vary", "Cookie");

        context.Response.Cookies.Append(
            CookieName,
            _protector.Protect(sessionId.ToString("N"), SessionLifetime),
            new CookieOptions
            {
                // No script ever needs to read this, and the Blazor client must not be able to;
                // HttpOnly keeps an XSS bug from turning into a session-theft bug.
                HttpOnly = true,

                // Lax lets the cookie ride a normal top-level navigation — someone following a link
                // to the demo keeps their cart — while withholding it from cross-site POSTs. This
                // does mean the storefront has to be served from the same site as the API; if the
                // WASM client is ever hosted on a different domain, the cookie would need
                // SameSite=None (and therefore Secure), which is a deliberate decision, not a
                // default worth drifting into.
                SameSite = SameSiteMode.Lax,

                Secure = _requireSecureCookie,

                // Not subject to a cookie-consent banner: without it the site cannot tell two
                // shoppers apart, which is the definition of strictly necessary.
                IsEssential = true,

                Path = "/",
                MaxAge = SessionLifetime,
            });

        return sessionId;
    }
}

/// <summary>
/// Places the demo session in the pipeline.
/// </summary>
public static class DemoSessionApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the demo-session cookie middleware. Call it before the endpoints: anything that queries
    /// carts or orders needs the session bound first, and a request that reaches an endpoint with
    /// no session bound will correctly — and unhelpfully — see nothing at all.
    /// </summary>
    public static IApplicationBuilder UseDemoSession(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<DemoSessionMiddleware>();
    }
}
