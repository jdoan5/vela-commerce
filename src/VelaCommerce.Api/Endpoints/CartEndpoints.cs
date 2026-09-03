// Everything in this file runs against a DbContext that has already been narrowed to one visitor
// by the DemoTenancy query filter, so no query below says "where this cart is mine". That is the
// point: the isolation is a property of the model, not of a WHERE clause somebody remembered to
// write, and a handler added here next year inherits it without knowing it exists. Two rules
// survive that filter and have to be honoured by hand, because a query filter restricts reads and
// nothing else:
//
//   * WRITES ARE NOT FILTERED. Constructing a Cart takes a session id, and the only acceptable
//     source for it is ICurrentDemoSession. Nothing here accepts a session, a cart id or an owner
//     from the request.
//   * CART LINES ARE NOT TENANTED. CartLine carries no session id and is deliberately not exposed
//     as a DbSet; every line below is reached through its cart, which is filtered.

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using VelaCommerce.Api.Contracts;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Common;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// Registration for the cart surface: read the cart, add to it, change a quantity, remove a line,
/// empty it.
/// <para>
/// Reads project into contracts and never materialise the aggregate. Writes do the opposite —
/// they load the tracked <see cref="Cart"/> and go through its methods — because quantity caps,
/// the merge rule and the single-currency rule are domain invariants, and re-implementing them in
/// a handler is how two places end up disagreeing about what a cart is allowed to hold. The
/// handler's job is to translate what the domain refuses into an HTTP status, not to second-guess
/// it.
/// </para>
/// </summary>
public static class CartEndpoints
{
    /// <summary>
    /// The <c>display_name</c> column is varchar(200). Product and variant names are each capped
    /// at 200 in the catalog, so the composed string can exceed it; clamping here turns a 500 from
    /// PostgreSQL into a slightly shortened label nobody will notice.
    /// </summary>
    private const int DisplayNameMaxLength = 200;

    /// <summary>
    /// Maps the cart group. Called by the host, so this file never has to know how the app is
    /// composed — and so the whole surface can be moved or removed in one line if the demo ever
    /// needs to run read-only.
    /// </summary>
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder app)
    {
        var cart = app
            .MapGroup("/api/cart")
            .WithTags("Cart")
            .AddEndpointFilter(PreventSharedCachingAsync);

        cart.MapGet("/", GetCartAsync)
            .WithName("GetCart")
            .WithSummary("Get the current visitor's cart")
            .WithDescription(
                "Always 200, never 404: a visitor who has never added anything gets an empty cart " +
                "rather than an error, and reading a cart never creates one, so browsing writes no " +
                "rows. Each line reports the price captured when it was added alongside the " +
                "catalog's current price, with 'priceChanged' and a signed 'priceDifference' when " +
                "the two disagree; lines are never silently repriced. A line whose variant has been " +
                "withdrawn reports 'stillInCatalog: false' and no current price. Which cart you get " +
                "is decided entirely by the signed session cookie.");

        cart.MapPost("/items", AddItemAsync)
            .WithName("AddCartItem")
            .WithSummary("Add a variant to the cart")
            .WithDescription(
                "The body carries a variant id and a quantity and nothing else - price, SKU and " +
                "display name are read from the catalog, so a client cannot name its own price. " +
                "Adding a variant already in the cart increases that line rather than appending a " +
                "second one, which makes the quantity an increment; use PATCH to set an absolute " +
                $"value. Responds 404 for a variant that is not in the live catalog and 400 when the " +
                $"domain refuses the change - a non-positive quantity, a resulting line above the " +
                $"per-line cap of {CartLine.MaxQuantity}, or an item priced in a currency this cart " +
                "is not in. The cart row is created on this call, not on a read. Returns the whole " +
                "cart so the caller needs no follow-up GET.")
            // The ProblemHttpResult arm of the union declares no status of its own - measured, not
            // assumed - because its status is a runtime value. Without these two lines the document
            // would advertise an endpoint that only ever succeeds, and a generated client would have
            // no type for the errors it will certainly meet.
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        cart.MapPatch("/items/{variantId:guid}", ChangeQuantityAsync)
            .WithName("ChangeCartItemQuantity")
            .WithSummary("Set a line's quantity")
            .WithDescription(
                "The quantity is absolute, not a delta, so a retried request cannot add a unit. " +
                "Zero removes the line. Responds 404 when that variant is not a line in this cart " +
                $"and 400 for a negative quantity or one above the per-line cap of " +
                $"{CartLine.MaxQuantity}. This endpoint never creates a cart. Returns the whole cart.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        cart.MapDelete("/items/{variantId:guid}", RemoveItemAsync)
            .WithName("RemoveCartItem")
            .WithSummary("Remove a line from the cart")
            .WithDescription(
                "Idempotent: removing a line that is already gone - or one from a cart that does " +
                "not exist - is 200 with the cart as it stands, not 404, because the caller's " +
                "desired end state is already true and a double-clicked remove button should not " +
                "produce an error. Never creates a cart. Returns the whole cart.");

        cart.MapDelete("/", ClearCartAsync)
            .WithName("ClearCart")
            .WithSummary("Empty the cart")
            .WithDescription(
                "Removes every line. The cart itself survives, so the next add does not have to " +
                "recreate it and the nightly demo reset still has one row per visitor to reap. " +
                "Idempotent, and never creates a cart. Returns the emptied cart.");

        return app;
    }

    /// <summary>
    /// Marks every cart response uncacheable by anything that is not this browser.
    /// <para>
    /// Applied to the group rather than written into each handler, so a cart endpoint added later
    /// cannot forget it. Without this, a CDN or reverse proxy in front of the demo is free to cache
    /// <c>GET /api/cart</c> — the response has no query string and no <c>Authorization</c> header,
    /// so it looks perfectly cacheable — and then serve one visitor's cart to the next. That would
    /// defeat the whole tenancy filter from outside the process, where none of its guarantees
    /// reach. <c>Vary: Cookie</c> is belt to <c>no-store</c>'s braces, for caches that treat
    /// <c>no-store</c> as advice.
    /// </para>
    /// </summary>
    private static async ValueTask<object?> PreventSharedCachingAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Vary = "Cookie";

        return await next(context);
    }

    private static async Task<Ok<CartResponse>> GetCartAsync(
        VelaCommerceDbContext db,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReadCartAsync(db, cancellationToken));

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> AddItemAsync(
        CartAddItemRequest request,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        CancellationToken cancellationToken)
    {
        // The one place in this file that needs the session id as a value rather than as an
        // invisible filter, because a new row has to be stamped with an owner. If there is no
        // session the answer is to refuse, not to invent one: a cart written with a placeholder
        // owner is either unreadable forever or, worse, readable by whoever the placeholder next
        // matches. Unreachable in the composed host, where the middleware binds a session before
        // any endpoint runs - which is exactly why it is written down rather than assumed.
        if (session.SessionId is not { } sessionId)
        {
            return NoDemoSessionProblem();
        }

        // Price, SKU and name all come from the catalog row. The request contributed the variant id
        // and the quantity, and that is the entire extent of the client's influence on what this
        // line will cost. The Product join is not decoration: it is required, so EF inner-joins it,
        // and Product's own soft-delete filter therefore hides variants of a withdrawn product -
        // which is the behaviour we want, since those must not be addable either.
        var variant = await db.ProductVariants
            .AsNoTracking()
            .Where(v => v.Id == request.VariantId && v.DeletedAt == null)
            .Select(v => new
            {
                v.Id,
                v.Sku,
                VariantName = v.Name,
                ProductName = v.Product!.Name,
                PriceAmount = v.Price.Amount,
                PriceCurrency = v.Price.Currency,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (variant is null)
        {
            return VariantNotFoundProblem(request.VariantId);
        }

        var cart = await LoadCartForWriteAsync(db, cancellationToken);
        if (cart is null)
        {
            // Created in the currency of the first thing put in it, rather than defaulting to USD
            // and then rejecting a EUR variant with a confusing 400 about a cart the shopper never
            // knowingly created. Added to the change tracker but not saved yet: if the domain
            // refuses the item below we return without SaveChanges, so a rejected first add still
            // leaves no row behind and reading stays free.
            cart = new Cart(sessionId, variant.PriceCurrency);
            db.Carts.Add(cart);
        }

        try
        {
            // Cart.AddItem validates before it mutates, so a throw here leaves the aggregate
            // exactly as it was and there is nothing to roll back.
            cart.AddItem(
                variant.Id,
                variant.Sku,
                ComposeDisplayName(variant.ProductName, variant.VariantName),
                new Money(variant.PriceAmount, variant.PriceCurrency),
                request.Quantity);
        }
        catch (DomainException exception)
        {
            return DomainProblem(exception);
        }

        // CONCURRENCY, MEASURED RATHER THAN GUESSED AT. Read-modify-write with no lock and no
        // concurrency token: two requests from one session both SELECT, both mutate their own
        // copy of the aggregate, both save. Two overlapping adds were run 30 times against real
        // PostgreSQL in each of the three states this endpoint can be in, and every one of the
        // three misbehaved on all 30 runs - so these are the normal outcomes of the race, not
        // unlucky ones:
        //
        //   * NO CART YET  -> two cart rows for one session (the index on demo_session_id is
        //     deliberately not unique). Reads take the newest, so one of the two adds is invisible
        //     from then on.
        //   * LINE ALREADY EXISTS -> lost update. Both read quantity 1, both write 2; the shopper
        //     clicked twice and the cart says 2 rather than 3. Nothing anywhere reports this.
        //   * CART EXISTS, LINE IS NEW -> two lines for one variant. Cart.AddItem's merge rule
        //     cannot fire, because neither transaction can see the other's uncommitted line, and
        //     ix_cart_lines_cart_id_variant_id is not unique, so both INSERTs land.
        //
        // Left unsolved in this phase on purpose, but not left quiet - and deliberately not
        // papered over on read: a duplicated line is something a tester can see and report,
        // whereas a read that quietly merged duplicates would hide the write until a checkout
        // total disagreed with the cart. Of the three, only the lost update is invisible on
        // screen, which is what makes it the one to fix first.
        //
        // The fixes belong with checkout, where money is actually taken and where the transaction
        // boundary is finally decided: a unique partial index on (cart_id, variant_id) plus
        // catch-and-retry closes the duplicate-line case, a unique index on carts.demo_session_id
        // closes the two-carts case, and only a row lock (SELECT ... FOR UPDATE on the cart) or a
        // rowversion token closes the lost update. Guessing at that transaction's shape before it
        // exists would be worse than writing down exactly what breaks until then.
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await ReadCartAsync(db, cancellationToken));
    }

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> ChangeQuantityAsync(
        Guid variantId,
        CartChangeQuantityRequest request,
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        var cart = await LoadCartForWriteAsync(db, cancellationToken);

        // The variant id addresses a LINE here, not a catalog row, so the existence check is
        // against the cart and not against ProductVariants. A line whose variant was withdrawn
        // after it was added is still a line, and the shopper must be able to change or remove it.
        if (cart is null || cart.Lines.All(line => line.VariantId != variantId))
        {
            return LineNotFoundProblem(variantId);
        }

        try
        {
            cart.ChangeQuantity(variantId, request.Quantity);
        }
        catch (DomainException exception)
        {
            return DomainProblem(exception);
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await ReadCartAsync(db, cancellationToken));
    }

    private static async Task<Ok<CartResponse>> RemoveItemAsync(
        Guid variantId,
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        var cart = await LoadCartForWriteAsync(db, cancellationToken);

        // Guarded so that a no-op DELETE costs one SELECT rather than a SELECT and a pointless
        // round trip to commit nothing.
        if (cart is not null && cart.Lines.Any(line => line.VariantId == variantId))
        {
            cart.RemoveItem(variantId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ReadCartAsync(db, cancellationToken));
    }

    private static async Task<Ok<CartResponse>> ClearCartAsync(
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        var cart = await LoadCartForWriteAsync(db, cancellationToken);

        if (cart is { IsEmpty: false })
        {
            // Clear() empties the aggregate's line collection; because the relationship is required
            // and cascades, EF turns the orphans into DELETEs rather than leaving detached rows.
            cart.Clear();
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ReadCartAsync(db, cancellationToken));
    }

    /// <summary>
    /// Loads the visitor's cart for mutation, tracked and with its lines.
    /// <para>
    /// No <c>WHERE demo_session_id = ...</c>: the DemoTenancy filter supplies it, and if it ever
    /// stopped supplying it, a hand-written clause here would hide the regression from every other
    /// query in the application. <c>FirstOrDefaultAsync</c> rather than <c>FindAsync</c> on
    /// purpose — <c>Find</c> can answer from the change tracker without ever composing a query,
    /// which means without ever applying the filter.
    /// </para>
    /// <para>
    /// A session can own more than one cart row (the index on <c>demo_session_id</c> is
    /// deliberately not unique), so "the cart" has to be defined rather than assumed. Newest wins,
    /// and newest is expressible as <c>ORDER BY id DESC</c> because ids are UUIDv7: the timestamp
    /// leads the bytes, and PostgreSQL compares <c>uuid</c> bytewise, so key order is creation
    /// order. Reads and writes both go through this method, so they cannot disagree about which
    /// cart they mean.
    /// </para>
    /// </summary>
    private static Task<Cart?> LoadCartForWriteAsync(
        VelaCommerceDbContext db,
        CancellationToken cancellationToken) =>
        db.Carts
            .Include(cart => cart.Lines)
            .OrderByDescending(cart => cart.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Builds the response for the visitor's cart, repricing against the live catalog as it goes.
    /// <para>
    /// Two queries rather than one correlated-subquery-per-line, which would fit in a single round
    /// trip. The trade is taken on purpose: the second query is skipped entirely when the cart is
    /// empty — the common case for a visitor who is only browsing, and the case that shares the
    /// catalog's cold-start budget — and when it does run it is one <c>= ANY(...)</c> against a
    /// primary key for a collection the domain caps at a handful of lines. What is bought is that
    /// both queries are ordinary enough to be obviously translatable, instead of depending on how
    /// far client evaluation reaches into a nested projection.
    /// </para>
    /// <para>
    /// Only the live <em>amount</em> is fetched. The currency is taken from the line, because
    /// <c>ProductVariant.Reprice</c> refuses to change a variant's currency, so the two cannot
    /// diverge — and a variant id is never reused, since ids are generated at construction.
    /// </para>
    /// </summary>
    private static async Task<CartResponse> ReadCartAsync(
        VelaCommerceDbContext db,
        CancellationToken cancellationToken)
    {
        var cart = await db.Carts
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Select(c => new
            {
                c.Currency,
                // Ordered by the line's own key, which is UUIDv7 and therefore chronological: the
                // cart lists items in the order they were added and never reshuffles under the
                // shopper between two renders. A merged add keeps its line's original position,
                // which is what makes "quantity went from 1 to 2" legible on screen.
                Lines = c.Lines
                    .OrderBy(line => line.Id)
                    .Select(line => new
                    {
                        line.VariantId,
                        line.Sku,
                        line.DisplayName,
                        UnitPriceAmount = line.UnitPrice.Amount,
                        UnitPriceCurrency = line.UnitPrice.Currency,
                        line.Quantity,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // No cart row is the ordinary state of a visitor who has only browsed, not an error, and
        // answering it does not create one.
        if (cart is null)
        {
            return CartResponse.Empty();
        }

        if (cart.Lines.Count == 0)
        {
            return CartResponse.Empty(cart.Currency);
        }

        var variantIds = cart.Lines.Select(line => line.VariantId).Distinct().ToArray();

        var livePrices = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variantIds.Contains(variant.Id) && variant.DeletedAt == null)
            .Select(variant => new { variant.Id, Amount = variant.Price.Amount })
            .ToDictionaryAsync(row => row.Id, row => row.Amount, cancellationToken);

        var lines = cart.Lines
            .Select(line => new CartLineResponse(
                line.VariantId,
                line.Sku,
                line.DisplayName,
                new MoneyDto(line.UnitPriceAmount, line.UnitPriceCurrency),
                line.Quantity,
                // Absent from the dictionary means the variant is no longer sellable. Reported as
                // null rather than as "unchanged", so the storefront can tell "the price is the
                // same" apart from "there is no price any more".
                livePrices.TryGetValue(line.VariantId, out var liveAmount)
                    ? new MoneyDto(liveAmount, line.UnitPriceCurrency)
                    : null))
            .ToList();

        return new CartResponse(cart.Currency, lines);
    }

    /// <summary>
    /// Joins the product and variant names into the label the cart shows, and clamps the result to
    /// what the column will hold. The variant name is optional in the domain (a product with one
    /// SKU often has nothing useful to add), so a bare product name is a normal outcome rather
    /// than a missing value to paper over.
    /// </summary>
    private static string ComposeDisplayName(string productName, string variantName)
    {
        var composed = string.IsNullOrWhiteSpace(variantName)
            ? productName
            : $"{productName} — {variantName}";

        if (composed.Length <= DisplayNameMaxLength)
        {
            return composed;
        }

        // Back off one character if the cut would land between a surrogate pair, which would
        // otherwise put half a code point into the column and render as a replacement glyph.
        var cut = DisplayNameMaxLength;
        if (char.IsHighSurrogate(composed[cut - 1]))
        {
            cut--;
        }

        return composed[..cut];
    }

    /// <summary>
    /// Turns a broken invariant into a 400. <see cref="DomainException"/> means the caller asked
    /// for something the domain forbids — a quantity of zero on an add, a line pushed past the cap,
    /// a second currency — which is a client error and not a server fault, and letting it reach the
    /// exception handler would report it as a 500 and hide a usable message behind a generic one.
    /// The domain's own wording is passed through because it already names the rule and the number:
    /// "Quantity is capped at 99 per line on the demo" is a better error than anything a handler
    /// could re-derive from the outside.
    /// </summary>
    private static ProblemHttpResult DomainProblem(DomainException exception) =>
        TypedResults.Problem(
            title: "That change to the cart is not allowed",
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult VariantNotFoundProblem(Guid variantId) =>
        TypedResults.Problem(
            title: "No such variant",
            detail: $"Variant {variantId} is not in the live catalog, so there is no price to add "
                    + "it at. A variant that has been withdrawn reads the same as one that never "
                    + "existed, on purpose.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult LineNotFoundProblem(Guid variantId) =>
        TypedResults.Problem(
            title: "Not a line in this cart",
            detail: $"Variant {variantId} is not in this cart, so there is no quantity to change. "
                    + "Use POST /api/cart/items to add it. Removing a line that may already be gone "
                    + "is DELETE, which is idempotent and does not report this.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The fail-closed branch for writes. Reads degrade gracefully without a session — the query
    /// filter shows an unidentified caller nothing, which is an empty cart — but a write has no
    /// safe degradation, because it would have to choose an owner for a new row. 500 rather than
    /// 401: there is no login to send anybody to, and a missing session here means the host was
    /// composed without the session middleware, which is the server's fault and not the visitor's.
    /// </summary>
    private static ProblemHttpResult NoDemoSessionProblem() =>
        TypedResults.Problem(
            title: "No demo session",
            detail: "This request has no demo session, so there is no visitor to own a cart. "
                    + "Writes refuse rather than guess an owner.",
            statusCode: StatusCodes.Status500InternalServerError);
}
