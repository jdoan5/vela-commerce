using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Admin;

/// <summary>
/// The one-click demo admin: a second cookie asserting a binding to one demo session.
/// <para>
/// <b>The cookie gates the feature. The model gates the data.</b> That distinction is the whole
/// design and it is the usual thing to get backwards. Every admin query runs through the same
/// <c>DemoTenancy</c>-filtered sets the storefront uses, so if this policy were deleted tomorrow an
/// anonymous caller reaching the reprice endpoint would still write only their own override rows,
/// and the orders page would still show only their own orders. Losing the credential would lose the
/// feature's front door, not its isolation — and the tests assert those two halves separately.
/// </para>
/// <para>
/// <b>It is a binding assertion, not a bearer token.</b> The ticket carries one claim, the demo
/// session it was issued for, and the policy demands that claim equal the session on the current
/// request. So <c>vela.admin</c> lifted into another browser is inert: that browser presents a
/// different <c>vela.session</c> and the equality fails. Copying both cookies is not an escalation
/// either — it is becoming that visitor, which is what the session isolation tests already cover.
/// </para>
/// <para>
/// <b>Not ASP.NET Core Identity</b>, which the plan originally named. Identity's subject is an
/// account, and this trust model has none: no account, no password, no user row. It would add seven
/// tables, a migration and a large DI graph to a host whose entire thesis is a cold start from zero
/// replicas, in order to authenticate a button that is deliberately public. The cost is real and
/// worth stating: it forecloses the passkey stretch goal, which is an Identity-template feature.
/// Adding it later is additive — the scheme name, the policy and the claim all survive.
/// </para>
/// <para>
/// <b>Not a hand-rolled protector either.</b> The framework's cookie handler already seals its
/// ticket with the <c>IDataProtectionProvider</c> this host has configured, so unforgeability and
/// the failure mode come free and are identical to the session cookie's: a lost key ring
/// invalidates both, and the next request re-mints. A second protector with its own purpose string
/// would be a second mechanism to reason about and a second place to get expiry wrong.
/// </para>
/// </summary>
public static class DemoAdminAuthentication
{
    /// <summary>The authentication scheme. Named rather than default, so nothing else is affected.</summary>
    public const string Scheme = "VelaAdmin";

    /// <summary>The authorization policy every admin route requires.</summary>
    public const string Policy = "DemoAdmin";

    /// <summary>Cookie name, in the same family as the session cookie so both read as this shop's.</summary>
    public const string CookieName = "vela.admin";

    /// <summary>The only claim on the ticket: the demo session this admin cookie was issued for.</summary>
    public const string SessionClaim = "sid";

    /// <summary>
    /// How long an admin sitting lasts. Short, and deliberately not sliding: this is a demo console,
    /// and a credential that renews itself for as long as somebody leaves a tab open is a credential
    /// nobody ever reconsiders.
    /// </summary>
    private static readonly TimeSpan Sitting = TimeSpan.FromHours(2);

    /// <param name="services">The host's collection.</param>
    /// <param name="requireSecureCookie">
    /// False only in Development, mirroring the session cookie. A Secure cookie is not returned over
    /// plain http, which would make the whole console silently unusable on a developer's machine.
    /// </param>
    public static IServiceCollection AddDemoAdmin(this IServiceCollection services, bool requireSecureCookie)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddAuthentication()
            .AddCookie(Scheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = requireSecureCookie
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.None;

                options.ExpireTimeSpan = Sitting;
                options.SlidingExpiration = false;

                // ANSWER, DO NOT REDIRECT. The default handler sends a browser to a login page that
                // does not exist here; there is no login, only a button. The write routes live under
                // /api and are called by forms and by tests, both of which want a status code. A 302
                // to /Account/Login would reach a Bruno assertion as a mystery.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new BoundToTheCallersSession()));

        // SCOPED, not singleton. The handler reads ICurrentDemoSession, which is scoped to the
        // request — a singleton holding it would capture whichever visitor happened to arrive first
        // and then authorise every later admin against that one session. The host validates scopes
        // in Development, so this would throw there and, far worse, would not in Production.
        services.AddScoped<IAuthorizationHandler, BoundToTheCallersSessionHandler>();

        return services;
    }

    /// <summary>Builds the ticket. One claim, and it is the session the caller already has.</summary>
    public static ClaimsPrincipal PrincipalFor(Guid sessionId) =>
        new(new ClaimsIdentity(
            [new Claim(SessionClaim, sessionId.ToString("N"))],
            Scheme,
            nameType: SessionClaim,
            roleType: null));
}

/// <summary>The requirement: this ticket must have been issued to the session presenting it.</summary>
public sealed class BoundToTheCallersSession : IAuthorizationRequirement;

/// <summary>
/// Compares the ticket's session against the one the middleware bound for this request.
/// <para>
/// Reading the session through <see cref="ICurrentDemoSession"/> rather than off the cookie is what
/// keeps the two mechanisms in step: the session middleware is the only thing that decides who a
/// caller is, and this handler agrees with it by construction rather than by parsing the same cookie
/// a second time and hoping the two parsers stay identical.
/// </para>
/// </summary>
internal sealed class BoundToTheCallersSessionHandler(ICurrentDemoSession session)
    : AuthorizationHandler<BoundToTheCallersSession>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BoundToTheCallersSession requirement)
    {
        // No session bound means no request identity to be an admin of. Fails closed, like the
        // query filter it sits beside.
        if (session.SessionId is not { } current)
        {
            return Task.CompletedTask;
        }

        var issuedFor = context.User.FindFirstValue(DemoAdminAuthentication.SessionClaim);

        if (string.Equals(issuedFor, current.ToString("N"), StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
