using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VelaCommerce.Api.Admin;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Api.Tenancy;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The admin console over real HTTP, and the claims it is only worth having if it keeps.
/// <para>
/// The console is public and unauthenticated in the ordinary sense — a button grants the cookie,
/// because a demo behind a password is a demo nobody looks at. What makes that defensible is that
/// the credential gates the FEATURE and the model gates the DATA, and those are separate
/// mechanisms that fail independently. These tests assert both halves separately, because the
/// tempting mistake is to prove one and assume the other.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AdminConsoleTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly Storefront _shop;

    public AdminConsoleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _shop = new Storefront(fixture);
    }

    public void Dispose() => _shop.Dispose();

    /// <summary>
    /// Pulls the hidden token Blazor renders into every admin form.
    /// <para>
    /// Fetched from the page rather than disabled in the test host, because a suite that turns
    /// antiforgery off is a suite that would not notice it being turned off in production.
    /// </para>
    /// </summary>
    private static async Task<string> TokenFromAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);

        // Attribute order is the renderer's business, so both arrangements are matched rather than
        // one being assumed and the test breaking on a framework update.
        const string pattern =
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\""
            + "|value=\"([^\"]+)\"[^>]*?name=\"__RequestVerificationToken\"";

        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);

        Assert.True(match.Success, $"No antiforgery token in the page at {path}. Blazor renders one per form.");

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string tokenPath, string action, params (string Key, string Value)[] fields)
    {
        var token = await TokenFromAsync(client, tokenPath);

        var body = fields
            .Append((Key: "__RequestVerificationToken", Value: token))
            .ToDictionary(field => field.Item1, field => field.Item2);

        return await client.PostAsync(action, new FormUrlEncodedContent(body));
    }

    /// <summary>
    /// Accepts an admin write, whether or not the client followed the redirect.
    /// <para>
    /// Every handler answers 303 so a reload cannot resubmit the form, but the factory's client
    /// follows redirects by default and hands back the 200 from the destination. Asserting the
    /// 303 alone would be asserting a client setting rather than the endpoint's behaviour.
    /// </para>
    /// </summary>
    private static void AssertAccepted(HttpResponseMessage response, string route) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.SeeOther || response.IsSuccessStatusCode,
            $"{route} answered {(int)response.StatusCode}, which is neither the 303 it sends nor "
            + "the 200 a redirect-following client would land on.");

    /// <summary>
    /// Signs a browser in as the demo admin for its own session.
    /// <para>
    /// Takes an existing client when the caller needs the admin and the orders to belong to the
    /// same visitor — the console is session-scoped, so an admin minted in a fresh browser can
    /// never see an order placed in another one. That is the tenancy working, and it makes a
    /// happy-path pack test impossible to write without this parameter.
    /// </para>
    /// </summary>
    private async Task<HttpClient> AdminAsync(HttpClient? existing = null)
    {
        var client = existing ?? _shop.Host.NewBrowser();

        // Establishes the demo session first: the ticket is issued for whoever is asking, so there
        // has to be somebody asking.
        using var warmup = await client.GetAsync("/api/cart");
        Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);

        using var signIn = await PostFormAsync(client, "/admin", "/api/admin/sign-in");
        AssertAccepted(signIn, "/api/admin/sign-in");

        // The proof, rather than the status code: the orders page is behind the policy, so a 200
        // here means the cookie was issued AND binds to this client's session.
        using var orders = await client.GetAsync("/admin/orders");
        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);

        return client;
    }

    private async Task<long?> OverrideAmountAsync(Guid variantId)
    {
        await using var db = _fixture.CreateContext();

        var rows = await db.Set<DemoCatalogPriceOverride>()
            .IgnoreQueryFilters()
            .Where(o => o.VariantId == variantId)
            .Select(o => (long?)o.PriceAmount)
            .ToListAsync();

        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>
    /// THE HEADLINE CLAIM, driven the way a reviewer would drive it: two browsers, two cookie jars,
    /// one shared catalog.
    /// </summary>
    [Fact]
    public async Task An_admin_reprice_in_one_session_is_invisible_to_another()
    {
        var jib = await _shop.StockAsync("Storm jib", onHand: 10);

        using var alice = await AdminAsync();
        var bob = await _shop.NewShopperAsync();

        using var repriced = await PostFormAsync(
            alice, "/admin/catalog", "/api/admin/catalog/override",
            ("variantId", jib.VariantId.ToString()),
            ("priceAmount", "100"));

        AssertAccepted(repriced, "/api/admin/catalog/override");

        // Alice's cart captures her price...
        await using var aliceCart = new HttpClientCart(alice);
        var alicePrice = await aliceCart.AddAndReadUnitPriceAsync(jib.VariantId);
        Assert.Equal(100, alicePrice);

        // ...and Bob's captures the shop's, from the same variant, at the same moment.
        await bob.AddToCartAsync(jib);
        var bobCart = await bob.CartAsync();
        Assert.False(bobCart.IsEmpty);

        var bobPrice = await BobUnitPriceAsync(bob);
        Assert.Equal(jib.UnitPriceMinorUnits, bobPrice);
    }

    private static async Task<long> BobUnitPriceAsync(Shopper shopper)
    {
        using var response = await shopper.Client.GetAsync("/api/cart");
        var body = await response.Content.ReadFromJsonAsync<CartPriceView>();

        Assert.NotNull(body);
        return body.Lines[0].UnitPrice.Amount;
    }

    /// <summary>
    /// The admin cookie lifted into another browser, which is the attack somebody will try.
    /// <para>
    /// Every other defence is deliberately satisfied first — the request carries a real admin
    /// ticket and an antiforgery pair minted for exactly the cookies it sends — so that the only
    /// thing left able to refuse it is the binding under test. A 400 here would mean antiforgery
    /// caught it and the binding was never reached, which is why the status is asserted and not
    /// merely the absence of success.
    /// </para>
    /// <para>
    /// Two assertions, and the second is the one people forget: the ticket is refused, AND it
    /// would have reached nothing if it had not been. The credential and the model are separate
    /// defences, so each is asserted on its own rather than one being taken as evidence of the
    /// other.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_admin_cookie_from_one_session_is_inert_in_another()
    {
        var porthole = await _shop.StockAsync("Bronze porthole", onHand: 5);

        var alice = await FreshAdminCookiesAsync();
        var bob = await FreshAdminCookiesAsync();

        using var lifted = _shop.Host.NewCookieWatchingClient();

        // The attacker's browser: their own demo session, Alice's admin cookie pasted in beside it.
        var stolen = $"{bob.Session}; {alice.Admin}";

        // Minted while carrying that pair, because ASP.NET Core binds the antiforgery field token
        // to the authenticated identity — a token fetched anonymously would be rejected as a
        // mismatch, and the test would pass for the wrong reason.
        var (antiforgeryCookie, token) = await AntiforgeryPairAsync(lifted, stolen);

        using var attempt = new HttpRequestMessage(HttpMethod.Post, "/api/admin/catalog/override");
        attempt.Headers.Add("Cookie", $"{stolen}; {antiforgeryCookie}");
        attempt.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["variantId"] = porthole.VariantId.ToString(),
            ["priceAmount"] = "1",
            ["__RequestVerificationToken"] = token,
        });

        using var refused = await lifted.SendAsync(attempt);

        // Alice's ticket names Alice's session; the request carries Bob's. Authentication succeeds
        // — the ticket is genuine — and authorization is what says no, which is a 403.
        Assert.True(
            refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"A lifted admin cookie was answered {(int)refused.StatusCode}. A 2xx or 303 means it "
            + "was accepted; a 400 means antiforgery refused it first and the session binding was "
            + "never exercised.");

        // And nothing was written under either session — the half that would still hold if the
        // policy were deleted tomorrow, because the model scopes the write regardless.
        Assert.Null(await OverrideAmountAsync(porthole.VariantId));
    }

    /// <summary>
    /// Sign-out answers the redirect it builds, and takes the cookie back.
    /// <para>
    /// This route had no test at all until the handler was found returning a blank 200: its only
    /// parameter is <see cref="HttpContext"/>, which makes it match <c>RequestDelegate</c>, and that
    /// overload discards the <see cref="IResult"/>. The cookie was cleared and the redirect was
    /// thrown away, so the button worked and went nowhere. Only the compiler noticed, and only on a
    /// full Release compile.
    /// </para>
    /// <para>
    /// Note what is deliberately NOT asserted: that the old ticket stops working. Cookie
    /// authentication has no server-side session to invalidate — signing out deletes the cookie
    /// from the browser, and a ticket someone had already copied stays valid until it expires on
    /// its own. Asserting revocation here would be asserting a property this scheme does not have.
    /// What stops a copied ticket is the session binding, which
    /// <see cref="An_admin_cookie_from_one_session_is_inert_in_another"/> covers.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Signing_out_redirects_to_the_console_and_takes_the_cookie_back()
    {
        var admin = await FreshAdminCookiesAsync();

        using var raw = _shop.Host.NewCookieWatchingClient();

        var carried = $"{admin.Session}; {admin.Admin}";
        var (antiforgeryCookie, token) = await AntiforgeryPairAsync(raw, carried);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sign-out");
        request.Headers.Add("Cookie", $"{carried}; {antiforgeryCookie}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        });

        using var response = await raw.SendAsync(request);

        // The status and the destination, separately: a 303 to nowhere would still fail the click.
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.ToString());

        // And the cookie is actually taken back rather than merely being redirected away from.
        var cleared = SetCookie(response, DemoAdminAuthentication.CookieName);
        Assert.Equal($"{DemoAdminAuthentication.CookieName}=", cleared);
    }

    /// <summary>
    /// The console's own banner must not believe a lifted cookie.
    /// <para>
    /// <c>/admin</c> is the one admin page without the policy attribute — it has to render for a
    /// visitor who has not signed in, since it is where they sign in. So it decided what to show
    /// from <c>User.Identity.IsAuthenticated</c>, and authentication is the half that a lifted
    /// ticket passes: the ticket is genuine, it is only issued for somebody else. The page then
    /// told an attacker "Signed in." and, in the same box, that the cookie "is checked against that
    /// session on every request. Copied into another browser it is inert" — which was true of every
    /// page except the one saying it.
    /// </para>
    /// <para>
    /// This needs its own test rather than a strengthened existing one:
    /// <see cref="AntiforgeryPairAsync"/> performs exactly this GET and asserts only that it is 200
    /// with a token in it, and both branches of the page render a form with a token.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_console_shows_a_lifted_cookie_the_sign_in_form_not_a_welcome()
    {
        var alice = await FreshAdminCookiesAsync();
        var bob = await FreshAdminCookiesAsync();

        using var lifted = _shop.Host.NewCookieWatchingClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
        request.Headers.Add("Cookie", $"{bob.Session}; {alice.Admin}");

        using var response = await lifted.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Both halves, because either alone is satisfiable by a page that renders nothing useful.
        Assert.DoesNotContain("Signed in.", html, StringComparison.Ordinal);
        Assert.Contains("Sign in as demo admin", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one thing the console leads with, which had no test at all.
    /// <para>
    /// Every pack assertion in this file was about a refusal — another session's order is a 404, a
    /// caller with no cookie is a 401. <c>PackAsync</c> could have stopped writing entirely and all
    /// 378 tests would have stayed green, while the README and ADR 0004 both describe packing as
    /// the admin's one order mutation.
    /// </para>
    /// <para>
    /// The stock assertion is the second half and not decoration: ADR 0004's whole argument for
    /// allowing pack is that it is the transition which touches no shared row. That is a claim
    /// about the ledger, so the ledger is what gets read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Packing_your_own_paid_order_moves_it_and_leaves_the_shared_ledger_alone()
    {
        var lantern = await _shop.StockAsync("Anchor lantern", onHand: 6);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(lantern);

        using var placed = await shopper.CheckoutAsync($"pack-{Guid.CreateVersion7():N}");
        var order = await ResponseReader.OrderAsync(placed);

        var before = await LedgerAsync(lantern.VariantId);

        using var admin = await AdminAsync(shopper.Client);

        using var packed = await PostFormAsync(
            admin, "/admin", $"/api/admin/orders/{order.OrderNumber}/pack");

        AssertAccepted(packed, "pack");

        Assert.Equal(OrderStatus.Packed, await StatusAsync(order.OrderNumber));
        Assert.Equal(before, await LedgerAsync(lantern.VariantId));
    }

    /// <summary>
    /// The stale write the claim exists to turn into an answer.
    /// <para>
    /// An admin reads Paid, the timeline worker takes the order Paid → Packed → Shipped, and the
    /// admin's write lands afterwards. Deleting the status predicate from the claim was measured
    /// rather than assumed, and the result is a <b>500, not a reverted order</b>:
    /// <c>OrderStateMachine</c> has no <c>Shipped → Packed</c> edge, so <c>MarkPacked</c> throws.
    /// The absent self-transitions are the backstop. What the claim adds is the difference between
    /// a stack trace and a 409 that tells the truth — and it covers the far more ordinary case too,
    /// where the worker merely packed the order a second earlier.
    /// </para>
    /// <para>
    /// So this test asserts the status code, not just "not success". A 500 here would mean the
    /// guard is gone and only the domain is catching it, which is precisely the state the endpoint
    /// was in before.
    /// </para>
    /// <para>
    /// The move to Shipped is made directly rather than by waiting on the worker, which this host
    /// now silences: a race test whose other party is a live background service on a 20-second
    /// dwell is a test that passes because nothing happened.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Packing_an_order_that_moved_on_is_refused_rather_than_reverting_it()
    {
        var cleat = await _shop.StockAsync("Bow shackle", onHand: 4);

        var shopper = await _shop.NewShopperAsync();
        await shopper.AddToCartAsync(cleat);

        using var placed = await shopper.CheckoutAsync($"revert-{Guid.CreateVersion7():N}");
        var order = await ResponseReader.OrderAsync(placed);

        using var admin = await AdminAsync(shopper.Client);

        // The token is fetched while the order is still Paid, exactly as a browser would have it:
        // the admin's page was rendered before the worker moved anything.
        var token = await TokenFromAsync(admin, "/admin");

        await AdvanceToShippedAsync(order.OrderNumber);

        using var attempt = await admin.PostAsync(
            $"/api/admin/orders/{order.OrderNumber}/pack",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);

        // The refusal is the visible half; this is the half that matters.
        Assert.Equal(OrderStatus.Shipped, await StatusAsync(order.OrderNumber));
    }

    /// <summary>Drives one order through the state machine out of band, the way the worker would.</summary>
    private async Task AdvanceToShippedAsync(string orderNumber)
    {
        await using var db = _fixture.CreateContext();

        var order = await db.Orders
            .IgnoreQueryFilters()
            .Include(entity => entity.Lines)
            .SingleAsync(entity => entity.OrderNumber == orderNumber);

        order.MarkPacked();
        order.MarkShipped();

        await db.SaveChangesAsync();
    }

    private async Task<OrderStatus> StatusAsync(string orderNumber)
    {
        await using var db = _fixture.CreateContext();

        return await db.Orders
            .IgnoreQueryFilters()
            .Where(entity => entity.OrderNumber == orderNumber)
            .Select(entity => entity.Status)
            .SingleAsync();
    }

    /// <summary>The shared stock row as a pair, so a change to either half fails the comparison.</summary>
    private async Task<(int OnHand, int Reserved)> LedgerAsync(Guid variantId)
    {
        await using var db = _fixture.CreateContext();

        var row = await db.StockItems
            .IgnoreQueryFilters()
            .Where(item => item.VariantId == variantId)
            .Select(item => new { item.OnHand, item.Reserved })
            .SingleAsync();

        return (row.OnHand, row.Reserved);
    }

    /// <summary>A demo visitor's two cookies: the session they were given, and the ticket they earned.</summary>
    private sealed record AdminBrowser(string Session, string Admin);

    /// <summary>
    /// Signs a fresh visitor in and hands back their raw cookie values.
    /// <para>
    /// Driven on a client that keeps no cookie jar and stops at the redirect, because both of
    /// those would otherwise hide the thing being collected: a jar swallows the header, and a
    /// followed redirect hands back the destination's response instead of the one that set it.
    /// </para>
    /// </summary>
    private async Task<AdminBrowser> FreshAdminCookiesAsync()
    {
        using var raw = _shop.Host.NewCookieWatchingClient();

        using var mint = await raw.GetAsync("/api/cart");
        var session = SetCookie(mint, DemoSessionMiddleware.CookieName);

        var (antiforgeryCookie, token) = await AntiforgeryPairAsync(raw, session);

        using var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sign-in");
        signIn.Headers.Add("Cookie", $"{session}; {antiforgeryCookie}");
        signIn.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        });

        using var granted = await raw.SendAsync(signIn);

        return new AdminBrowser(session, SetCookie(granted, DemoAdminAuthentication.CookieName));
    }

    /// <summary>
    /// Fetches an antiforgery cookie and matching field token valid for exactly the cookies given.
    /// The two halves are useless apart, so they are always collected together.
    /// </summary>
    private async Task<(string Cookie, string Token)> AntiforgeryPairAsync(HttpClient raw, string cookieHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
        request.Headers.Add("Cookie", cookieHeader);

        using var response = await raw.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(token), "The admin page rendered no antiforgery token.");

        return (SetCookie(response, "Antiforgery", prefixMatch: false), token);
    }

    /// <summary>
    /// Pulls one <c>Set-Cookie</c> off a response and fails with the response's own headers when it
    /// is not there, because "value is null" is not a diagnosis.
    /// </summary>
    private static string SetCookie(HttpResponseMessage response, string name, bool prefixMatch = true)
    {
        var all = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];

        var match = all.FirstOrDefault(value => prefixMatch
            ? value.StartsWith($"{name}=", StringComparison.Ordinal)
            : value.Contains(name, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            match is not null,
            $"No {name} cookie on the {(int)response.StatusCode} from {response.RequestMessage?.RequestUri}. "
            + $"Set-Cookie carried: {(all.Length == 0 ? "nothing" : string.Join(" | ", all.Select(v => v.Split('=')[0])))}.");

        return match!.Split(';')[0];
    }

    /// <summary>
    /// Every write route, with no admin cookie at all. Catches a route added later to the wrong
    /// group — the failure that looks like nothing until somebody notices the console needs no
    /// sign-in.
    /// </summary>
    [Theory]
    [InlineData("/api/admin/catalog/reprice")]
    [InlineData("/api/admin/catalog/override")]
    [InlineData("/api/admin/catalog/overrides/clear")]
    [InlineData("/api/admin/orders/VELA-AAAAAAA/pack")]
    public async Task Every_admin_write_refuses_a_caller_with_no_admin_cookie(string route)
    {
        var client = _shop.Host.NewBrowser();

        using var warmup = await client.GetAsync("/api/cart");

        using var response = await client.PostAsync(route, new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"{route} answered {(int)response.StatusCode} to a caller with no admin cookie.");

        client.Dispose();
    }

    /// <summary>
    /// Packing somebody else's order is a 404, not a 403 — because the row was never loaded. A 403
    /// would confirm the order exists, which would make this endpoint a way to discover order
    /// numbers.
    /// </summary>
    [Fact]
    public async Task Packing_another_sessions_order_is_a_404_rather_than_a_403()
    {
        var jib = await _shop.StockAsync("Deck cleat", onHand: 5);

        var bob = await _shop.NewShopperAsync();
        await bob.AddToCartAsync(jib);

        using var placed = await bob.CheckoutAsync($"admin-{Guid.CreateVersion7():N}");
        var order = await ResponseReader.OrderAsync(placed);

        using var alice = await AdminAsync();

        // The token comes from /admin rather than /admin/orders: Alice's orders page is empty —
        // that is the point of the test — so it renders no form and therefore no token. An
        // antiforgery token is scoped to the session, not to the form it was rendered into.
        using var attempt = await PostFormAsync(
            alice, "/admin", $"/api/admin/orders/{order.OrderNumber}/pack");

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    /// <summary>
    /// The demo reset must take this visitor's overrides and leave everybody else's, which is the
    /// same tenancy question the rest of the reset already answers.
    /// </summary>
    [Fact]
    public async Task A_demo_reset_clears_this_sessions_overrides_and_only_this_sessions()
    {
        var mine = await _shop.StockAsync("Signal lamp", onHand: 5);
        var theirs = await _shop.StockAsync("Chart weight", onHand: 5);

        using var alice = await AdminAsync();
        using var bob = await AdminAsync();

        using var _ = await PostFormAsync(alice, "/admin/catalog", "/api/admin/catalog/override",
            ("variantId", mine.VariantId.ToString()), ("priceAmount", "111"));

        using var __ = await PostFormAsync(bob, "/admin/catalog", "/api/admin/catalog/override",
            ("variantId", theirs.VariantId.ToString()), ("priceAmount", "222"));

        Assert.Equal(111, await OverrideAmountAsync(mine.VariantId));
        Assert.Equal(222, await OverrideAmountAsync(theirs.VariantId));

        using var reset = await alice.PostAsync("/api/demo/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        Assert.Null(await OverrideAmountAsync(mine.VariantId));
        Assert.Equal(222, await OverrideAmountAsync(theirs.VariantId));
    }
}

/// <summary>Adds a line and reports what the cart captured for it.</summary>
internal sealed class HttpClientCart(HttpClient client) : IAsyncDisposable
{
    public async Task<long> AddAndReadUnitPriceAsync(Guid variantId)
    {
        using var added = await client.PostAsJsonAsync("/api/cart/items", new { variantId, quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var body = await added.Content.ReadFromJsonAsync<CartPriceView>();
        Assert.NotNull(body);

        return body.Lines[0].UnitPrice.Amount;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The slice of the cart response these tests read.</summary>
internal sealed record CartPriceView(IReadOnlyList<CartPriceLineView> Lines, bool IsEmpty);

internal sealed record CartPriceLineView(Guid VariantId, MoneyView UnitPrice);
