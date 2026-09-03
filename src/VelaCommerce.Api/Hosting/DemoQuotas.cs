using System.Globalization;

using Microsoft.EntityFrameworkCore;

using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Api.Hosting;

/// <summary>
/// How many rows one visitor may leave behind in a shared demo database.
/// <para>
/// Rate limiting bounds how <em>fast</em> somebody can write; this bounds how <em>much</em> they
/// can accumulate. The two are not substitutes. A patient script writing one row a second, well
/// inside every limiter here, still fills a free-tier database in a weekend — and the reason to
/// care is not disk space but that the demo is the portfolio: an unavailable database is a broken
/// link on a CV.
/// </para>
/// <para>
/// The numbers are generous enough that no reviewer will ever meet one. A cart of forty distinct
/// SKUs and twenty-five orders is far past what anybody exploring a shop does, and the ceiling
/// exists for the case where somebody is not exploring.
/// </para>
/// </summary>
/// <param name="MaxCartsPerSession">
/// Cart rows one session may own. Effectively a tripwire rather than a working limit: the cart
/// endpoint creates a row only when the session has none, so the only way past one is the
/// documented two-carts race, and the index on <c>demo_session_id</c> is deliberately not unique.
/// It is enforced anyway, because "no endpoint can currently do that" is a fact about today's
/// endpoints and not a property of the data.
/// </param>
/// <param name="MaxLinesPerCart">
/// Lines in the cart a shopper is adding to. This is the cap that does real work: a line is one
/// row per distinct variant, the catalog holds several hundred, and nothing about the domain stops
/// a script adding every one of them. Quantity is already capped at 99 per line by
/// <c>CartLine.MaxQuantity</c>, so this is the other axis.
/// </param>
/// <param name="MaxOrdersPerSession">
/// Orders one session may place. The expensive cap: every order drags order lines, stock
/// reservations and — for the asynchronous payment scenarios — outbox rows behind it, so an order
/// is worth several rows and a settlement round trip.
/// </param>
internal sealed record DemoQuotaOptions(
    int MaxCartsPerSession,
    int MaxLinesPerCart,
    int MaxOrdersPerSession)
{
    /// <summary>Configuration section. Colon-separated, matching every other option group in this solution.</summary>
    public const string SectionName = "Demo:Quotas";

    /// <summary>The shipped numbers, used whenever configuration is absent or unusable.</summary>
    public static DemoQuotaOptions Defaults { get; } = new(
        MaxCartsPerSession: 5,
        MaxLinesPerCart: 40,
        MaxOrdersPerSession: 25);

    /// <summary>
    /// Reads the section, falling back per key to the default for anything absent or unusable.
    /// <para>
    /// Hand-bound and deliberately incapable of throwing, matching <c>PaymentSimulatorOptions</c>
    /// and <c>OutboxOptions</c>. Build-time OpenAPI generation composes this host, so refusing to
    /// start over a mistyped quota would turn a harmless configuration slip into a red build.
    /// </para>
    /// </summary>
    public static DemoQuotaOptions Read(IConfiguration? configuration, ILogger? logger) => new(
        ReadPositive(configuration, logger, nameof(MaxCartsPerSession), Defaults.MaxCartsPerSession),
        ReadPositive(configuration, logger, nameof(MaxLinesPerCart), Defaults.MaxLinesPerCart),
        ReadPositive(configuration, logger, nameof(MaxOrdersPerSession), Defaults.MaxOrdersPerSession));

    private static int ReadPositive(IConfiguration? configuration, ILogger? logger, string key, int fallback)
    {
        var configured = configuration?[$"{SectionName}:{key}"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        logger?.LogWarning(
            "{Key} is '{Value}', which is not a positive whole number. Falling back to {Fallback}.",
            $"{SectionName}:{key}",
            configured,
            fallback);

        return fallback;
    }
}

/// <summary>
/// Refuses a write that would push one visitor past their row allowance, before the endpoint that
/// would have written it runs.
/// <para>
/// <strong>Why a middleware and not an endpoint filter.</strong> A filter is the better shape and
/// is not available: the cart and checkout groups are composed in
/// <c>CartEndpoints.MapCartEndpoints</c> and <c>CheckoutEndpoints.MapCheckoutEndpoints</c>, which
/// this slice does not own, and adding one would mean two agents editing the same registration in
/// the same phase. A middleware placed after the session is bound reaches the same requests
/// without touching either file. The cost is that the routes are matched by method and path here
/// rather than inherited from the group, so <see cref="CartItemsPath"/> and
/// <see cref="CheckoutPath"/> have to be kept honest — they are the two literals in this file that
/// a route rename would silently break, which is why they are named constants and why the summary
/// for this slice calls them out.
/// </para>
/// <para>
/// <strong>Why not a database trigger.</strong> A trigger would be unbypassable, which is the
/// property this design gives up. It was rejected on cost of failure rather than on elegance: a
/// trigger raises inside whatever transaction is running, so hitting the order cap would abort a
/// checkout mid-flight and surface as a <c>DbUpdateException</c> in a handler that has carefully
/// enumerated the three <c>DbUpdateException</c>s it expects. Turning that into a clean 409 would
/// mean teaching every current and future writer about a quota. Refusing before the transaction
/// opens keeps the failure in one place and keeps it cheap.
/// </para>
/// <para>
/// The check is not atomic, and does not need to be: two adds racing past a cap of forty leave a
/// cart with forty-one lines, which is a rounding error against the thing being prevented. What it
/// must never do is refuse a legitimate shopper, which is why every count is read through the
/// tenancy filter and a caller with no session is waved through to the endpoint's own refusal.
/// </para>
/// </summary>
internal static class DemoQuotas
{
    /// <summary>The one cart route that can grow the row count. PATCH and DELETE only ever shrink it.</summary>
    private static readonly PathString CartItemsPath = new("/api/cart/items");

    /// <summary>The route that creates orders.</summary>
    private static readonly PathString CheckoutPath = new("/api/checkout");

    /// <summary>
    /// Applies the caps, or does nothing at all for a request that cannot create rows.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the request may proceed, or the refusal to send instead.
    /// </returns>
    public static async Task<IResult?> EvaluateAsync(
        HttpContext context,
        DemoQuotaOptions quotas,
        CancellationToken cancellationToken)
    {
        var request = context.Request;

        if (!HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        var addingToCart = Addresses(request.Path, CartItemsPath);
        var checkingOut = Addresses(request.Path, CheckoutPath);

        if (!addingToCart && !checkingOut)
        {
            return null;
        }

        // Resolved per request from the request's own scope, which is the same scope the endpoint
        // will resolve it from — so this is one extra query on an existing connection rather than a
        // second context. GetService rather than GetRequiredService: a host composed without
        // persistence has no rows to cap, and this middleware must not be the thing that breaks it.
        if (context.RequestServices.GetService<VelaCommerceDbContext>() is not { } db)
        {
            return null;
        }

        return addingToCart
            ? await EvaluateCartAsync(db, quotas, cancellationToken)
            : await EvaluateCheckoutAsync(db, quotas, cancellationToken);
    }

    /// <summary>
    /// Whether this request path addresses the given route.
    /// <para>
    /// A segment comparison rather than string equality, because ASP.NET Core's route matcher
    /// treats <c>/api/checkout/</c> as <c>/api/checkout</c> and is case-insensitive. A quota that
    /// compared strings would be bypassed by a trailing slash — the request would reach the
    /// endpoint and this middleware would have decided it was not interesting. Anything left over
    /// beyond a bare slash is a different route and is not ours to cap.
    /// </para>
    /// </summary>
    private static bool Addresses(PathString path, PathString route) =>
        path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase, out var remaining)
        && (!remaining.HasValue || remaining.Value is "/");

    /// <summary>
    /// Counts this visitor's carts and the lines in the one they are about to write to.
    /// <para>
    /// ONE query answers both questions. <c>OrderByDescending(cart.Id)</c> is not decoration: it is
    /// the same ordering <c>CartEndpoints.LoadCartForWriteAsync</c> uses to decide which cart "the
    /// cart" means — newest wins, and newest is expressible as id order because ids are UUIDv7 — so
    /// the first count in the list belongs to exactly the cart the add would land in. Any other
    /// ordering would cap a different cart from the one being written, which is the kind of bug
    /// that only appears for the visitor unlucky enough to own two.
    /// </para>
    /// <para>
    /// No <c>WHERE demo_session_id = ...</c>: the DemoTenancy filter supplies it. A caller with no
    /// session therefore counts zero and is allowed through — correctly, because the endpoint
    /// behind this refuses a session-less write itself, and a quota is not the right place to
    /// discover that the host is misconfigured.
    /// </para>
    /// </summary>
    private static async Task<IResult?> EvaluateCartAsync(
        VelaCommerceDbContext db,
        DemoQuotaOptions quotas,
        CancellationToken cancellationToken)
    {
        var lineCounts = await db.Carts
            .AsNoTracking()
            .OrderByDescending(cart => cart.Id)
            .Select(cart => cart.Lines.Count)
            .ToListAsync(cancellationToken);

        if (lineCounts.Count >= quotas.MaxCartsPerSession)
        {
            return Refusal(
                "Too many carts on this demo session",
                $"This session already owns {lineCounts.Count} cart(s), which is the demo's limit "
                + $"of {quotas.MaxCartsPerSession}. Nothing has been added.");
        }

        // A session with no cart yet is about to create one holding a single line, so there is
        // nothing to compare. The list is ordered newest-first, so index 0 is the cart the endpoint
        // will load.
        if (lineCounts.Count > 0 && lineCounts[0] >= quotas.MaxLinesPerCart)
        {
            return Refusal(
                "That cart is full",
                $"This cart already holds {lineCounts[0]} lines, which is the demo's limit of "
                + $"{quotas.MaxLinesPerCart} distinct items. Remove a line, or reset your demo "
                + "data. Changing the quantity of a line you already have is unaffected - the "
                + "limit is on distinct items, not on units.");
        }

        return null;
    }

    /// <summary>
    /// Counts this visitor's orders. One indexed count against
    /// <c>ix_orders_demo_session_id_placed_at</c>, and no session id written by hand.
    /// </summary>
    private static async Task<IResult?> EvaluateCheckoutAsync(
        VelaCommerceDbContext db,
        DemoQuotaOptions quotas,
        CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().CountAsync(cancellationToken);

        if (orders < quotas.MaxOrdersPerSession)
        {
            return null;
        }

        return Refusal(
            "That is enough orders for one demo session",
            $"This session has placed {orders} orders, which is the demo's limit of "
            + $"{quotas.MaxOrdersPerSession}. Your cart is untouched and nothing has been charged - "
            + "nothing here ever is.");
    }

    /// <summary>
    /// The one refusal shape, and the reason it is 409 rather than 429.
    /// <para>
    /// 429 says "you are going too fast, wait and try again", and waiting will not help here: the
    /// rows are already written and time does not remove them. 409 says the request conflicts with
    /// the state of the resource, which is exactly true, and it points at the one action that
    /// resolves it. Every message ends by naming <c>POST /api/demo/reset</c> because a limit a
    /// visitor cannot clear themselves is a dead end on a demo nobody is supervising.
    /// </para>
    /// </summary>
    private static IResult Refusal(string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail + " Press \"Reset my demo data\" in the banner, or POST /api/demo/reset, "
                    + "to clear this session's carts and orders and start over.",
            statusCode: StatusCodes.Status409Conflict);
}
