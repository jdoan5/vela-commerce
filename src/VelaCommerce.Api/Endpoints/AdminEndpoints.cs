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
            //
        // This read answers "whose order is this", and nothing else. It deliberately does not
        // decide whether the order may be packed, because by the time that decision was acted on it
        // would be a decision about a row nobody was holding.
        var owned = await db.Orders
            .AsNoTracking()
            .Where(entity => entity.OrderNumber == normalized)
            .Select(entity => (Guid?)entity.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (owned is not { } orderId)
        {
            return NoSuchOrder();
        }

        // The admin is the SECOND writer to orders.status. OrderTimelineWorker is the first, and it
        // states the rule this used to break: the status has to be part of the claim rather than
        // checked afterwards in C#, because checking it BEFORE the lock - which is the natural
        // thing to write, and what was written here - is not the same thing at all.
        //
        // WHAT THE MISSING CLAIM ACTUALLY COST, since the first version of this comment guessed and
        // guessed wrong. It was not silent corruption. An admin request that read Paid and then
        // paused while the worker took the order Paid -> Packed -> Shipped comes back to call
        // MarkPacked on a Shipped order, and OrderStateMachine has no Shipped -> Packed edge, so it
        // throws: an unhandled exception and a 500, on every stale path, including the far more
        // ordinary one where the worker merely packed it first. Measured by deleting the predicate
        // and watching the test - the answer is 500, not a reverted order.
        //
        // That is the absent self-transitions earning their keep, and it is worth being precise
        // about: the state machine is what makes this loud, and the claim below is what makes it
        // CORRECT. There is no concurrency token on Order to fall back on - xmin appears in this
        // repository only as evidence in the Demo Lab, never as a mapped row version - so without
        // the claim there is nothing between a stale read and a stack trace.
        // Wrapped in the execution strategy, which is not optional: Npgsql is configured to retry
        // on transient faults, and a retrying strategy refuses a transaction it did not open -
        // "does not support user-initiated transactions", as a 500 rather than a compile error.
        // ChangeTracker.Clear() is inside the lambda for the same reason it is in the refund path:
        // a retry that inherits entities tracked by the attempt that failed is a retry of something
        // other than what was asked for.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async Task<IResult> (CancellationToken token) =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(token);

            var paid = (int)OrderStatus.Paid;

            // FOR UPDATE, and deliberately NOT the worker's SKIP LOCKED. A worker that skips a locked
            // row loses nothing - it sweeps again in a second. A person who clicked a button wants an
            // answer about their order, so this one waits for the lock and then reports what it found:
            // either it packs, or the status has moved on and the 409 below is true rather than a guess
            // about who is holding the row.
        //
            // IgnoreQueryFilters() with no arguments, for the reason OrderTimelineWorker.ClaimAsync
            // spells out: leave the filters on and EF wraps the statement in a subquery, which buries
            // the locking clause and makes the claim stop being a claim. Dropping tenancy here is safe
            // precisely because the query above already established the order is this caller's - the id
            // came through the filter, and an id is not a capability anyone else can guess.
            var claimed = await db.Orders
                .FromSql(
                    $"""
                     SELECT *
                     FROM orders
                     WHERE id = {orderId}
                       AND status = {paid}
                       AND deleted_at IS NULL
                     FOR UPDATE
                     """)
                .IgnoreQueryFilters()
                .ToListAsync(token);

            if (claimed.Count == 0)
            {
                return TypedResults.Problem(
                    title: "That order cannot be packed",
                    detail: "Only a Paid order can be packed, and the timeline worker packs orders "
                            + "on its own schedule too - so this is the ordinary answer when it got "
                            + "there first. Reload the console to see where the order actually is.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Through the state machine, never an UPDATE on the column. OrderStateMachine has no
            // self-transitions on purpose, so a second pack throws instead of silently succeeding, and
            // writing the column in SQL would throw that alarm away - the same argument the worker
            // makes for itself, and it applies to a button at least as much as to a sweep.
            claimed[0].MarkPacked();
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            return SeeOther("/admin/orders");
        }, cancellationToken);
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
