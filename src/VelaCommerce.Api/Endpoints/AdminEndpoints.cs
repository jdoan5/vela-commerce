using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Api.Admin;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Everything the admin console writes.
/// <para>
/// <b>In <c>VelaCommerce.Api.Endpoints</c> rather than beside the rest of the admin</b>, because it
/// names the <c>DbContext</c> and an architecture rule allows exactly four places to do that. It
/// caught this file sitting in <c>VelaCommerce.Api.Admin</c> on its first run, which is the rule
/// working: the console's pages take projections and the console's writes live where every other
/// endpoint group lives, so there is no fifth namespace holding a unit of work.
/// </para>
/// <para>
/// <b>Pages at <c>/admin</c>, writes at <c>/api/admin</c>, and the split is load-bearing.</b> The
/// demo's rate limiter partitions on paths beginning <c>/api</c>, so mounting the writes there puts
/// every admin action behind the same per-session budget as a checkout, with no change to the
/// limiter and no second list of routes to keep in step with this one.
/// </para>
/// <para>
/// Every handler answers <b>303 See Other</b> rather than a body. These are browser form posts, and
/// 303 is what turns a POST into a GET on the redirect — so a reload of the resulting page does not
/// re-submit the form, and the shopper's back button does not offer to reprice the catalog again.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var open = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .AddEndpointFilter(ValidateAntiforgeryAsync)
            .ExcludeFromDescription();

        // Sign-in and sign-out are the two routes that cannot require the policy: one exists to
        // obtain the credential and the other to discard it.
        open.MapPost("/sign-in", SignInAsync);

        // The cast is load-bearing, and its absence is silent. SignOutAsync's only parameter is
        // HttpContext, which makes it match RequestDelegate, and MapPost prefers that overload over
        // the route-handler one. RequestDelegate returns Task, so the IResult this method builds is
        // constructed and dropped: the cookie is still cleared - the await runs - but the caller
        // gets a blank 200 instead of the 303 that sends them back to /admin. It compiles, it half
        // works, and only ASP0016 under -warnaserror says so. SignInAsync escapes it by accident,
        // by taking a second parameter.
        open.MapPost("/sign-out", (Delegate)SignOutAsync);

        var guarded = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .AddEndpointFilter(ValidateAntiforgeryAsync)
            .RequireAuthorization(DemoAdminAuthentication.Policy)
            .ExcludeFromDescription();

        guarded.MapPost("/orders/{orderNumber}/pack", PackAsync);
        guarded.MapPost("/catalog/reprice", RepriceAsync);
        guarded.MapPost("/catalog/override", OverrideAsync);
        guarded.MapPost("/catalog/overrides/clear", ClearAsync);

        return app;
    }

    /// <summary>
    /// Validates the hidden token Blazor's <c>AntiforgeryToken</c> component renders into each form.
    /// <para>
    /// Belt and braces rather than the only defence: both cookies are <c>SameSite=Lax</c>, so a
    /// cross-site POST arrives carrying neither and fails closed before this filter is reached. This
    /// is the layer that still holds if a future change relaxes that, and it costs one lookup.
    /// </para>
    /// </summary>
    private static async ValueTask<object?> ValidateAntiforgeryAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return TypedResults.Problem(
                title: "That form submission could not be verified",
                detail: "The anti-forgery token was missing or stale. Reload the admin page and try "
                        + "again - a token is minted per page, and one from a page left open across "
                        + "a restart will not verify.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }

    /// <summary>
    /// Grants the admin cookie to the caller's own session.
    /// <para>
    /// <b>It takes no input, and that is the security property.</b> There is no field naming a
    /// session, so there is nothing to tamper with: the ticket is issued for whoever is asking,
    /// read from the middleware that already decided who that is. A sign-in that accepted a session
    /// id would be an impersonation endpoint with a friendly name.
    /// </para>
    /// </summary>
    private static async Task<IResult> SignInAsync(HttpContext http, ICurrentDemoSession session)
    {
        if (session.SessionId is not { } sessionId)
        {
            return TypedResults.Problem(
                title: "No demo session",
                detail: "The admin console is scoped to a demo session and this request carries "
                        + "none. Visit the shop first, which is what mints one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await http.SignInAsync(
            DemoAdminAuthentication.Scheme,
            DemoAdminAuthentication.PrincipalFor(sessionId));

        return SeeOther("/admin/orders");
    }

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        await http.SignOutAsync(DemoAdminAuthentication.Scheme);
        return SeeOther("/admin");
    }

    /// <summary>
    /// Advances one of the caller's own orders from Paid to Packed.
    /// <para>
    /// The only order mutation the admin has, and deliberately not "mark shipped": shipping is the
    /// one fulfilment step that writes the SHARED stock ledger, and an admin that never writes a
    /// shared row is a claim worth more than a second button.
    /// </para>
    /// </summary>
    private static async Task<IResult> PackAsync(
        string orderNumber,
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        if (!OrderNumbers.TryNormalize(orderNumber, out var normalized))
        {
            return NoSuchOrder();
        }

        // No WHERE on the session: the DemoTenancy filter supplies it, so another visitor's order is
        // not found rather than forbidden. The difference matters - a 403 would confirm the order
        // exists, and this endpoint must not be usable to discover order numbers.
        var order = await db.Orders
            .FirstOrDefaultAsync(entity => entity.OrderNumber == normalized, cancellationToken);

        if (order is null)
        {
            return NoSuchOrder();
        }

        if (order.Status is not OrderStatus.Paid)
        {
            return TypedResults.Problem(
                title: "That order cannot be packed",
                detail: $"The order is {order.Status}. Only a Paid order can be packed, and the "
                        + "timeline worker packs orders on its own schedule too - so this is the "
                        + "ordinary answer when it got there first.",
                statusCode: StatusCodes.Status409Conflict);
        }

        order.MarkPacked();
        await db.SaveChangesAsync(cancellationToken);

        return SeeOther("/admin/orders");
    }

    private static async Task<IResult> RepriceAsync(
        HttpContext http,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var form = await http.Request.ReadFormAsync(cancellationToken);

        var category = form["category"].ToString();
        if (string.IsNullOrWhiteSpace(category))
        {
            return BadForm("Choose a category to reprice.");
        }

        if (!int.TryParse(form["percent"], out var percent) || percent is < -50 or > 50)
        {
            return BadForm("The percentage must be a whole number between -50 and 50.");
        }

        await db.RepriceCategoryAsync(
            session.SessionId!.Value, category, percent, clock.GetUtcNow(), cancellationToken);

        return SeeOther("/admin/catalog");
    }

    private static async Task<IResult> OverrideAsync(
        HttpContext http,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var form = await http.Request.ReadFormAsync(cancellationToken);

        if (!Guid.TryParse(form["variantId"], out var variantId))
        {
            return BadForm("That variant id is not a recognisable id.");
        }

        if (!long.TryParse(form["priceAmount"], out var priceAmount) || priceAmount < 0)
        {
            return BadForm("A price must be a whole number of minor units, and not negative.");
        }

        // Checked against the catalog rather than trusted: an override for a variant that does not
        // exist would be a row nothing could ever resolve, sitting in the table until the reset.
        var exists = await db.ProductVariants
            .AsNoTracking()
            .AnyAsync(v => v.Id == variantId && v.DeletedAt == null, cancellationToken);

        if (!exists)
        {
            return BadForm("There is no such variant on sale.");
        }

        await db.SetOverrideAsync(
            session.SessionId!.Value, variantId, priceAmount, clock.GetUtcNow(), cancellationToken);

        return SeeOther("/admin/catalog");
    }

    private static async Task<IResult> ClearAsync(VelaCommerceDbContext db, CancellationToken cancellationToken)
    {
        await db.ClearOverridesAsync(cancellationToken);
        return SeeOther("/admin/catalog");
    }

    /// <summary>
    /// 303, not 302. After a POST it is the status that tells a browser to follow with a GET, so a
    /// reload of the destination cannot resubmit the form behind it.
    /// </summary>
    private static IResult SeeOther(string location) =>
        Results.Extensions.SeeOther(location);

    private static IResult NoSuchOrder() =>
        TypedResults.Problem(
            title: "No such order",
            detail: "No order with that number belongs to this visitor. Somebody else's order and "
                    + "an order that does not exist answer identically, so this cannot be used to "
                    + "find out which order numbers are real.",
            statusCode: StatusCodes.Status404NotFound);

    private static IResult BadForm(string detail) =>
        TypedResults.Problem(
            title: "That form cannot be accepted",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>Adds the 303 the minimal API surface has no built-in for.</summary>
internal static class SeeOtherResultExtensions
{
    public static IResult SeeOther(this IResultExtensions _, string location) =>
        new SeeOtherResult(location);

    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
