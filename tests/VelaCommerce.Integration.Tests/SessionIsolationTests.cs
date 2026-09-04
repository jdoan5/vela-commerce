using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using VelaCommerce.Api.Tenancy;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Infrastructure.Persistence;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence that the shared demo does not leak between visitors.
/// <para>
/// This project is deployed once and browsed by strangers at the same time, with no login. The
/// only thing that makes two of them different people is a signed cookie, which means the cookie
/// is a credential and "whose cart is this" is a security question rather than a routing one. The
/// tests below are written to be read: each one states a way a visitor could end up holding
/// someone else's cart — no cookie, a cookie they edited, a cookie they guessed, an id they read
/// off a page — and shows what actually happens instead.
/// </para>
/// <para>
/// Every one of them goes over HTTP into the composed host. That is the point rather than an
/// affectation: the isolation is produced by four things cooperating (the cookie, the middleware
/// that decrypts it, the scoped holder it binds, and the query filter that reads that holder while
/// EF is translating), and a test that reached past any of them would be certifying a system that
/// is not the one on the internet.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SessionIsolationTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly DemoSessionHost _host;

    public SessionIsolationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _host = new DemoSessionHost(fixture.ConnectionString);
    }

    /// <summary>Disposes the host, and with it every client and the in-memory key ring.</summary>
    public void Dispose() => _host.Dispose();

    // ---------------------------------------------------------------------------------------
    // 1. The headline: two people shopping at the same time.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Two visitors, two carts, one database, one process — and neither sees the other.
    /// </summary>
    [Fact]
    public async Task Two_visitors_shopping_at_once_never_see_each_others_cart()
    {
        var lamp = await AddCatalogVariantAsync("Chart table lamp");
        var barometer = await AddCatalogVariantAsync("Brass barometer");

        var anna = await NewVisitorAsync();
        var boris = await NewVisitorAsync();

        // Two visitors really are two visitors: the host minted a separate session for each.
        Assert.NotEqual(anna.Cookie, boris.Cookie);

        var annaAfterAdd = await AddItemAsync(anna.Client, lamp.VariantId, quantity: 2);
        var borisAfterAdd = await AddItemAsync(boris.Client, barometer.VariantId, quantity: 5);

        // Every mutation answers with the whole cart, so a leak would already be visible in the
        // response to the write, before anybody reads anything back.
        Assert.Equal(new[] { lamp.Sku }, SkusIn(annaAfterAdd));
        Assert.Equal(new[] { barometer.Sku }, SkusIn(borisAfterAdd));

        // And again on the read a storefront actually makes, which takes a different code path
        // through the endpoint: a projection rather than a tracked aggregate.
        var annaCart = await GetCartAsync(anna.Client);
        var borisCart = await GetCartAsync(boris.Client);

        Assert.Equal(new[] { lamp.Sku }, SkusIn(annaCart));
        Assert.Equal(2, annaCart.TotalQuantity);

        Assert.Equal(new[] { barometer.Sku }, SkusIn(borisCart));
        Assert.Equal(5, borisCart.TotalQuantity);

        // The half of the claim that a passing API test cannot make on its own. Both carts are
        // sitting in one table, reachable from one connection, distinguished by nothing but a
        // column — so the separation above is the filter doing its job, not an artefact of one
        // of the two writes having quietly failed to land.
        await using var db = _fixture.CreateContext();
        var rows = await db.Carts
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Include(cart => cart.Lines)
            .Where(cart => cart.Lines.Any(line => line.Sku == lamp.Sku || line.Sku == barometer.Sku))
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(cart => cart.DemoSessionId).Distinct().Count());
    }

    // ---------------------------------------------------------------------------------------
    // 2. Fail closed.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A request that identifies nobody is shown nothing, and is shown it calmly.
    /// <para>
    /// Worth being precise about what "no session" means at this layer, because the HTTP edge and
    /// the data layer fail closed in two different ways and only one of them is visible here. A
    /// request arriving with no cookie does not stay session-less: the middleware mints a fresh
    /// session for it, so what this test proves is that a brand-new visitor sees an empty cart
    /// rather than the last person's. The genuinely unbound case — a scope where nothing ever
    /// called <c>Bind</c> — cannot be produced through HTTP at all, and is covered at the
    /// DbContext level in <see cref="DemoTenancyQueryFilterTests"/>.
    /// </para>
    /// <para>
    /// The assertion is on the empty result and not on an exception on purpose. An endpoint that
    /// threw would still be safe, but it would push every caller into a special case and would
    /// tempt the next person to "fix" it by relaxing the filter. Empty is the correct answer to
    /// "what is in the cart of a visitor who has never added anything", and it happens to be the
    /// same answer as "you have not told me who you are".
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_request_carrying_no_session_cookie_sees_an_empty_cart_not_somebody_elses()
    {
        var lamp = await AddCatalogVariantAsync("Chart table lamp");

        var anna = await NewVisitorAsync();
        await AddItemAsync(anna.Client, lamp.VariantId, quantity: 3);

        using var response = await SendWithCookieAsync(HttpMethod.Get, "/api/cart", cookieValue: null);
        await AssertCartSurfaceIsMappedAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cart = await ReadCartAsync(response);
        Assert.True(cart.IsEmpty);
        Assert.Empty(cart.Lines);

        // The unidentified caller was given an identity of their own rather than borrowing one.
        var issued = ReadSessionCookie(response);
        Assert.NotNull(issued);
        Assert.NotEqual(anna.Cookie, issued);

        // Anna is undisturbed: an anonymous read is a read, not a reset.
        Assert.Equal(3, (await GetCartAsync(anna.Client)).TotalQuantity);
    }

    // ---------------------------------------------------------------------------------------
    // 3. Tampering.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An edited cookie buys a fresh empty cart, never somebody else's.
    /// <para>
    /// The cookie is the whole of the authentication story here, and the raw string is the one
    /// input on the request that a visitor controls completely. Each case below is a different way
    /// of handing the server something it did not write; all of them have to land in the same
    /// place, because the value of a signed cookie is precisely that "not ours" is a single
    /// outcome rather than a family of interesting ones.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("flip-a-character")]
    [InlineData("truncated")]
    [InlineData("not-base64-at-all")]
    [InlineData("empty")]
    public async Task An_edited_session_cookie_is_discarded_rather_than_believed(string tampering)
    {
        var lamp = await AddCatalogVariantAsync("Chart table lamp");

        var anna = await NewVisitorAsync();
        await AddItemAsync(anna.Client, lamp.VariantId, quantity: 4);

        var forged = tampering switch
        {
            // One character of ciphertext, swapped for another legal base64url character. The
            // payload still looks entirely plausible; it simply no longer authenticates.
            "flip-a-character" => FlipOneCharacter(anna.Cookie),

            // Length attack: chop the tail and hope the reader is lenient about what it accepts.
            "truncated" => anna.Cookie[..(anna.Cookie.Length - 8)],

            // The unsubtle version, and the one a curious visitor actually tries first.
            "not-base64-at-all" => "let-me-in-please",

            // A present-but-blank cookie, which is a different code path from an absent one.
            "empty" => string.Empty,

            _ => throw new ArgumentOutOfRangeException(nameof(tampering), tampering, "Unknown tampering case."),
        };

        Assert.NotEqual(anna.Cookie, forged);

        using var response = await SendWithCookieAsync(HttpMethod.Get, "/api/cart", forged);
        await AssertCartSurfaceIsMappedAsync(response);

        // Not a 400 and certainly not a 500: an unreadable cookie is not an error condition, it is
        // simply somebody the server has not met. Treating it as a fault would turn a stale cookie
        // after a key rotation into an unusable site.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cart = await ReadCartAsync(response);
        Assert.True(cart.IsEmpty);

        // A new cookie was issued, which is how we know the forgery was thrown away rather than
        // accepted and merely happening to match nothing.
        var issued = ReadSessionCookie(response);
        Assert.NotNull(issued);
        Assert.NotEqual(forged, issued);
        Assert.NotEqual(anna.Cookie, issued);

        Assert.Equal(4, (await GetCartAsync(anna.Client)).TotalQuantity);
    }

    /// <summary>
    /// The strongest form of the tampering question: the attacker already knows the victim's
    /// session id and it still does not help.
    /// <para>
    /// Ids leak. They end up in logs, in a screenshot, in a support ticket, in a URL somebody
    /// pasted into chat. So the interesting test is not whether a session id is hard to guess —
    /// it is a UUIDv7 with 74 random bits, so it is — but whether knowing one is sufficient. It
    /// is not, and that is the whole difference between a signed cookie and a cookie. Here the id
    /// is read straight out of the database, giving the attacker perfect knowledge, and the cookie
    /// is written in exactly the format the middleware itself writes: the plaintext payload,
    /// missing only the signature they cannot produce.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Knowing_another_visitors_session_id_is_not_enough_to_use_it()
    {
        var lamp = await AddCatalogVariantAsync("Chart table lamp");

        var anna = await NewVisitorAsync();
        await AddItemAsync(anna.Client, lamp.VariantId, quantity: 7);

        Guid annaSession;
        await using (var db = _fixture.CreateContext())
        {
            annaSession = await db.Carts
                .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                .Where(cart => cart.Lines.Any(line => line.Sku == lamp.Sku))
                .Select(cart => cart.DemoSessionId)
                .SingleAsync();
        }

        Assert.NotEqual(Guid.Empty, annaSession);

        // Both spellings, because guessing the format is the attacker's next move after learning
        // the value: "N" is what the middleware puts inside the protected payload, "D" is what a
        // log line or a database client would have shown them.
        foreach (var attempt in new[] { annaSession.ToString("N"), annaSession.ToString("D") })
        {
            using var response = await SendWithCookieAsync(HttpMethod.Get, "/api/cart", attempt);
            await AssertCartSurfaceIsMappedAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await ReadCartAsync(response)).IsEmpty);
        }

        Assert.Equal(7, (await GetCartAsync(anna.Client)).TotalQuantity);
    }

    // ---------------------------------------------------------------------------------------
    // 4. Persistence.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The same cookie means the same cart, and the cookie is the only thing that decides it.
    /// <para>
    /// Replayed on a client with no cookie jar of its own, so the identity demonstrably travels in
    /// the header and nowhere else — not in a connection, not in server-side state keyed off
    /// anything the first client had. That the replay gets no new <c>Set-Cookie</c> is the other
    /// half: the middleware recognised the session rather than silently minting a replacement,
    /// which is the failure that would look exactly like "the cart keeps emptying itself" in
    /// production.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_same_cookie_on_a_later_request_finds_the_same_cart()
    {
        var lamp = await AddCatalogVariantAsync("Chart table lamp");

        var anna = await NewVisitorAsync();
        await AddItemAsync(anna.Client, lamp.VariantId, quantity: 6);

        // Same browser, second request.
        var again = await GetCartAsync(anna.Client);
        Assert.Equal(new[] { lamp.Sku }, SkusIn(again));
        Assert.Equal(6, again.TotalQuantity);

        // Fresh connection, no cookie jar, cookie supplied by hand.
        using var response = await SendWithCookieAsync(HttpMethod.Get, "/api/cart", anna.Cookie);
        await AssertCartSurfaceIsMappedAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var replayed = await ReadCartAsync(response);
        Assert.Equal(new[] { lamp.Sku }, SkusIn(replayed));
        Assert.Equal(6, replayed.TotalQuantity);

        Assert.Null(ReadSessionCookie(response));
    }

    // ---------------------------------------------------------------------------------------
    // 5. Direct-id probing.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Knowing which variant is in someone else's cart does not let you touch their cart.
    /// <para>
    /// Worth saying plainly what is and is not being probed, because the shape of the cart API is
    /// itself part of the defence. <strong>No cart endpoint accepts a cart id or a session id</strong>
    /// — not in a route, not in a query string, not in a body. <c>CartResponse</c> deliberately
    /// carries no identifier, so there is no key for a client to send back and therefore no
    /// decision for the server to make about whether to trust one. The only identifier a client
    /// supplies anywhere on this surface is a <em>variant</em> id, which is public catalog data
    /// and is owned by nobody.
    /// </para>
    /// <para>
    /// That still leaves a real question, and this is it. The two write endpoints keyed by variant
    /// id address a <em>line</em>, and a line does live inside somebody's cart. So an attacker who
    /// can see the catalog — everyone — can aim PATCH and DELETE at a variant they have good
    /// reason to think is in another visitor's cart. What they get back has to be their own cart's
    /// answer, not the victim's, and the victim's cart has to be untouched afterwards.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_visitor_cannot_reach_another_cart_by_addressing_a_variant_they_know_is_in_it()
    {
        var lamp = await AddCatalogVariantAsync("Chart table lamp");
        var barometer = await AddCatalogVariantAsync("Brass barometer");

        var anna = await NewVisitorAsync();
        var boris = await NewVisitorAsync();

        await AddItemAsync(anna.Client, lamp.VariantId, quantity: 2);
        await AddItemAsync(boris.Client, barometer.VariantId, quantity: 1);

        // Boris tries to run Anna's line up to the per-line cap.
        using (var patch = await boris.Client.PatchAsJsonAsync($"/api/cart/items/{lamp.VariantId}", new { quantity = 99 }))
        {
            await AssertCartSurfaceIsMappedAsync(patch);

            // 404, because that variant is not a line in *his* cart. The endpoint never learns
            // that it is a line in somebody else's, which is exactly the right amount for it to
            // know: an answer that distinguished "not yours" from "does not exist" would be an
            // oracle for probing what other visitors are shopping for.
            Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        }

        // Boris tries to delete it. DELETE is idempotent by design, so this is a 200 — and the
        // interesting assertion is not the status but what the 200 contains and what it changed.
        using (var delete = await boris.Client.DeleteAsync($"/api/cart/items/{lamp.VariantId}"))
        {
            await AssertCartSurfaceIsMappedAsync(delete);
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

            var borisCart = await ReadCartAsync(delete);
            Assert.Equal(new[] { barometer.Sku }, SkusIn(borisCart));
        }

        // And the blunt instrument: empty the cart. His, as it turns out.
        using (var clear = await boris.Client.DeleteAsync("/api/cart"))
        {
            await AssertCartSurfaceIsMappedAsync(clear);
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            Assert.True((await ReadCartAsync(clear)).IsEmpty);
        }

        var annaCart = await GetCartAsync(anna.Client);
        Assert.Equal(new[] { lamp.Sku }, SkusIn(annaCart));
        Assert.Equal(2, annaCart.TotalQuantity);
    }

    // ---------------------------------------------------------------------------------------
    // The credential itself.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The cookie's flags, asserted because they are load-bearing rather than cosmetic.
    /// <para>
    /// This cookie is the only credential on the site, so each attribute is holding something up.
    /// Without <c>HttpOnly</c> an XSS bug becomes a session-theft bug and every test above stops
    /// meaning anything. Without <c>Secure</c> the credential rides a plaintext hop.
    /// <c>SameSite</c> keeps another origin's form post from acting as the visitor. A missing
    /// <c>Max-Age</c> would make it a session cookie, and the cart would vanish with the tab. None
    /// of these can be caught by looking at a cart, which is why they are checked here.
    /// </para>
    /// <para>
    /// <c>Secure</c> doubles as the assertion that this host really is composed as Production: the
    /// middleware relaxes that flag only for local development, so a false here would mean every
    /// test above had quietly been exercising the wrong configuration.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_session_cookie_is_issued_as_a_credential_and_not_as_a_convenience()
    {
        var client = _host.NewRawClient();

        // Any endpoint will do: the middleware runs for the whole pipeline, health probes included.
        using var response = await client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookie = SetCookieHeaderValue
            .ParseList([.. response.Headers.GetValues(HeaderNames.SetCookie)])
            .Single(candidate => candidate.Name.Equals(DemoSessionMiddleware.CookieName, StringComparison.Ordinal));

        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(SameSiteMode.Lax, cookie.SameSite);
        Assert.Equal("/", cookie.Path.ToString());
        Assert.Equal(TimeSpan.FromDays(14), cookie.MaxAge);
    }

    // ---------------------------------------------------------------------------------------
    // Plumbing.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One browser: a client with its own cookie jar, plus the raw cookie value the host handed it
    /// so the tampering tests have something authentic to corrupt. The client is owned by the
    /// factory and disposed with it.
    /// </summary>
    private sealed record Visitor(HttpClient Client, string Cookie);

    /// <summary>
    /// Only the fields these tests reason about. Deliberately not the API's own
    /// <c>CartResponse</c>: the point of a wire-level test is to assert on what actually crossed
    /// the wire, and sharing the server's type would let a rename pass unnoticed on both sides.
    /// </summary>
    private sealed record CartView(string Currency, IReadOnlyList<CartLineView> Lines, int TotalQuantity, bool IsEmpty);

    private sealed record CartLineView(Guid VariantId, string Sku, int Quantity);

    /// <summary>
    /// Opens a browser and confirms it starts empty, which is the precondition every test above
    /// would otherwise have to assume.
    /// </summary>
    private async Task<Visitor> NewVisitorAsync()
    {
        var client = _host.NewBrowser();

        using var response = await client.GetAsync("/api/cart");
        await AssertCartSurfaceIsMappedAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadCartAsync(response)).IsEmpty);

        var cookie = ReadSessionCookie(response);
        Assert.NotNull(cookie);

        return new Visitor(client, cookie);
    }

    /// <summary>
    /// Puts a real, sellable variant in the catalog for a test to shop for.
    /// <para>
    /// Written through a session-less context, which is safe and worth noticing: products carry no
    /// <c>DemoSessionId</c> and are not tenanted, so the catalog is genuinely shared while carts
    /// are not. Every row is uniquely named, so tests never collide over the container they share.
    /// </para>
    /// </summary>
    private async Task<(Guid VariantId, string Sku)> AddCatalogVariantAsync(string name)
    {
        await using var db = _fixture.CreateContext();

        var product = new Product($"iso-{Guid.CreateVersion7():N}", name, "Written by the isolation tests.", "isolation");
        var variant = product.AddVariant($"ISO-{Guid.CreateVersion7():N}"[..20], "One size", new Money(4_500));

        db.Products.Add(product);
        await db.SaveChangesAsync();

        return (variant.Id, variant.Sku);
    }

    /// <summary>
    /// The SKUs a visitor can see, as a set the assertion can print. Asserted as a whole
    /// collection rather than pulled out with <c>Single()</c> so that a failure names the SKU that
    /// leaked instead of only complaining that there was more than one of something — the message
    /// is what a reviewer will read the day this actually breaks.
    /// </summary>
    private static string[] SkusIn(CartView cart) => [.. cart.Lines.Select(line => line.Sku)];

    private async Task<CartView> GetCartAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/cart");
        await AssertCartSurfaceIsMappedAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadCartAsync(response);
    }

    private async Task<CartView> AddItemAsync(HttpClient client, Guid variantId, int quantity)
    {
        using var response = await client.PostAsJsonAsync("/api/cart/items", new { variantId, quantity });

        await AssertCartSurfaceIsMappedAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadCartAsync(response);
    }

    /// <summary>
    /// Issues a request with the <c>Cookie</c> header set by hand — or deliberately absent — on a
    /// client that has no cookie jar to second-guess it.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithCookieAsync(HttpMethod method, string path, string? cookieValue)
    {
        var client = _host.NewRawClient();

        using var request = new HttpRequestMessage(method, path);
        if (cookieValue is not null)
        {
            request.Headers.Add(HeaderNames.Cookie, $"{DemoSessionMiddleware.CookieName}={cookieValue}");
        }

        return await client.SendAsync(request);
    }

    private static async Task<CartView> ReadCartAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<CartView>()
        ?? throw new InvalidOperationException("A cart endpoint answered with a null JSON body.");

    private static string? ReadSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues(HeaderNames.SetCookie, out var setCookies)
            ? SetCookieHeaderValue.ParseList([.. setCookies])
                .Where(cookie => cookie.Name.Equals(DemoSessionMiddleware.CookieName, StringComparison.Ordinal))
                .Select(cookie => cookie.Value.ToString())
                .FirstOrDefault()
            : null;

    /// <summary>
    /// Swaps one character of the protected payload for another legal one, so the forgery is a
    /// well-formed base64url string that simply fails its integrity check — the case that would
    /// slip past a reader which only validated the shape of the cookie.
    /// </summary>
    private static string FlipOneCharacter(string cookie)
    {
        var characters = cookie.ToCharArray();
        var middle = characters.Length / 2;
        characters[middle] = characters[middle] == 'A' ? 'B' : 'A';

        return new string(characters);
    }

    /// <summary>
    /// Turns "the cart endpoints are not in the pipeline" into a sentence instead of a wall of
    /// puzzling 404s. Both a routing miss and the cart's own 404s render as
    /// <c>application/problem+json</c> — AddProblemDetails sees to that — so content type
    /// cannot tell them apart. This helper is only ever called on responses from routes the
    /// cart is supposed to own, so any 404 here means the surface is missing.
    /// </summary>
    private static async Task AssertCartSurfaceIsMappedAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.NotFound)
            return;

        // Both a routing miss and the cart's own 404s render as application/problem+json, so
        // the content type cannot tell them apart. What separates them is the body: every 404
        // the cart raises itself carries a `detail` explaining which variant and why, while the
        // 404 the router produces for an unmapped path has none. A legitimate "that variant is
        // not a line in your cart" must not be mistaken for a missing endpoint — one of these
        // tests deliberately provokes exactly that.
        var body = await response.Content.ReadAsStringAsync();
        var hasDetail = !string.IsNullOrWhiteSpace(body)
                        && body.Contains("\"detail\"", StringComparison.Ordinal);

        if (!hasDetail)
        {
            Assert.Fail(
                "The cart endpoints are not mapped: a request came back as a bare routing 404 "
                + "with no problem detail. Program.cs needs `app.MapCartEndpoints();` alongside "
                + "`app.MapCatalogEndpoints();`. These tests drive the composed host "
                + "deliberately, so they cannot stand in their own routing for the real host's.");
        }
    }
}
