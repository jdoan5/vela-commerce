using System.Net;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The evidence behind the confirmation link: it opens the order for whoever holds it, and for
/// nobody else.
/// <para>
/// Two credentials coexist on this endpoint and they are not the same kind of thing. The session
/// cookie is an ambient identity — it arrives on every request, says who the caller is, and grants
/// them everything they own. The retrieval token is a bearer capability — it names one order, it
/// expires, and it says nothing about who is holding it. A shopper needs the second one because a
/// receipt has to survive a cleared cookie, a different device and being forwarded to whoever is
/// paying; and the store needs the first one because widening the cookie to cover other people's
/// orders would be a hole and minting a cookie for a stranger who followed a link would adopt them
/// into somebody else's cart.
/// </para>
/// <para>
/// So the tests come in pairs: what the token opens, and what it does not. The refusals all answer
/// 404 — never 403, never a different code for "expired" than for "not yours" — because any
/// difference between those answers is a way to find out which order numbers exist.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderRetrievalLinkTests : IDisposable
{
    private readonly Storefront _shop;

    public OrderRetrievalLinkTests(PostgresFixture fixture) => _shop = new Storefront(fixture);

    /// <summary>Disposes the host, its clients and the in-memory key ring.</summary>
    public void Dispose() => _shop.Dispose();

    /// <summary>
    /// The link works for a visitor who has their own session, and for one who has no session at
    /// all — which between them are every way a forwarded confirmation email can be opened.
    /// </summary>
    [Fact]
    public async Task A_signed_link_opens_the_order_for_someone_who_never_had_the_session()
    {
        var (buyer, order) = await BuyAsync("Sailmaker's palm");

        // A different visitor, with a session of their own and no claim to this order.
        var stranger = await _shop.NewShopperAsync();

        using (var response = await stranger.Client.GetAsync(order.RetrievalPath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var opened = await ResponseReader.OrderAsync(response);
            Assert.Equal(order.OrderNumber, opened.OrderNumber);
            Assert.Equal("Paid", opened.Status);
            Assert.Equal(order.Total.Amount, opened.Captured.Amount);

            // The gateway's answer is reported once, on the checkout that asked it, and is not
            // persisted anywhere for a later read to find. The durable facts are on the order.
            Assert.Null(opened.Payment);

            // Every response mints a fresh token, because Data Protection ciphertext is randomised
            // and a stable token would be a stable secret. Two different strings, both valid.
            Assert.NotEqual(order.RetrievalToken, opened.RetrievalToken);

            using var again = await stranger.Client.GetAsync(opened.RetrievalPath);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        // And a caller carrying no cookie jar at all: the case a link opened in a private window,
        // or by a mail client's link checker, actually produces.
        using var raw = _shop.Host.NewRawClient();
        using var sessionless = await raw.GetAsync(order.RetrievalPath);

        Assert.Equal(HttpStatusCode.OK, sessionless.StatusCode);
        Assert.Equal(order.OrderNumber, (await ResponseReader.OrderAsync(sessionless)).OrderNumber);

        // The buyer, meanwhile, still reads their own order the ordinary way: no token, just the
        // session that placed it.
        using var mine = await buyer.Client.GetAsync($"/api/orders/{order.OrderNumber}");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
    }

    /// <summary>
    /// Without a token, an order is visible only to the session that placed it.
    /// </summary>
    [Fact]
    public async Task Without_a_token_an_order_is_invisible_to_everyone_else()
    {
        var (_, order) = await BuyAsync("Rigging knife");

        var stranger = await _shop.NewShopperAsync();

        using var response = await stranger.Client.GetAsync($"/api/orders/{order.OrderNumber}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A token that has been edited opens nothing, and does so quietly.
    /// <para>
    /// The forgery is a well-formed base64url string with one character changed, which is the case
    /// a reader that only validated the <em>shape</em> of the token would let through. It fails its
    /// integrity check instead. The answer is 404 rather than 400 or 500: a tampered token is not a
    /// server error and not a malformed request, it is simply not a token, and the caller learns
    /// nothing from the difference.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tampered_token_opens_nothing()
    {
        var (_, order) = await BuyAsync("Brass hurricane lamp");

        var stranger = await _shop.NewShopperAsync();

        foreach (var token in new[] { FlipOneCharacter(order.RetrievalToken), "not-a-token", string.Empty })
        {
            using var response = await stranger.Client.GetAsync(
                $"/api/orders/{order.OrderNumber}?token={Uri.EscapeDataString(token)}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>
    /// A token is a capability for one order, not a key to the orders table: presenting a valid
    /// token for one order against another order's number opens neither.
    /// <para>
    /// Worth having its own test because the token payload is only an id — nothing about it forces
    /// the endpoint to compare it against the number in the route, and an implementation that
    /// forgot to would return somebody else's order to anyone holding any valid link.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_valid_token_for_another_order_opens_neither()
    {
        var (_, mine) = await BuyAsync("Chart divider");
        var (_, theirs) = await BuyAsync("Parallel rule");

        var stranger = await _shop.NewShopperAsync();

        using var crossed = await stranger.Client.GetAsync(
            $"/api/orders/{mine.OrderNumber}?token={Uri.EscapeDataString(theirs.RetrievalToken)}");

        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);

        // The token still works for the order it was actually issued for, so the refusal above is
        // the number check doing its job rather than the token having been invalidated.
        using var theirOwn = await stranger.Client.GetAsync(theirs.RetrievalPath);
        Assert.Equal(HttpStatusCode.OK, theirOwn.StatusCode);
    }

    /// <summary>
    /// Every way of failing to reach an order answers identically, so the endpoint cannot be used
    /// to discover which order numbers exist.
    /// <para>
    /// A malformed number, a well-formed number nobody has been given, and a real order belonging
    /// to somebody else are three very different situations on the server and one situation to the
    /// caller. If they differed — 400 here, 404 there, 403 for the third — an attacker could walk
    /// the number space and learn the store's order count, which is exactly what scrambling the
    /// numbers was meant to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_missing_order_and_someone_elses_order_answer_identically()
    {
        var (_, order) = await BuyAsync("Tide clock");

        var stranger = await _shop.NewShopperAsync();

        var answers = new List<ProblemView>();

        foreach (var path in new[]
                 {
                     "/api/orders/not-an-order-number",
                     "/api/orders/VELA-ZZZZZZZ",
                     $"/api/orders/{order.OrderNumber}",
                 })
        {
            using var response = await stranger.Client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            answers.Add(await ResponseReader.ProblemAsync(response));
        }

        // Compared field by field rather than as raw bodies: the framework adds a trace id to every
        // problem document, and that one really does differ per request.
        Assert.Single(answers.Select(answer => answer.Title).Distinct(StringComparer.Ordinal));
        Assert.Single(answers.Select(answer => answer.Detail).Distinct(StringComparer.Ordinal));
        Assert.Single(answers.Select(answer => answer.Status).Distinct());
    }

    /// <summary>
    /// Buys one unit of a freshly stocked item and returns the shopper and their order.
    /// </summary>
    private async Task<(Shopper Buyer, OrderView Order)> BuyAsync(string productName)
    {
        var variant = await _shop.StockAsync(productName, onHand: 5);

        var buyer = await _shop.NewShopperAsync();
        await buyer.AddToCartAsync(variant);

        using var response = await buyer.CheckoutAsync($"retrieval-{Guid.CreateVersion7():N}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (buyer, await ResponseReader.OrderAsync(response));
    }

    /// <summary>
    /// Swaps one character of the protected payload for another legal one, so the forgery is a
    /// well-formed base64url string that fails its integrity check rather than its parser.
    /// </summary>
    private static string FlipOneCharacter(string token)
    {
        var characters = token.ToCharArray();
        var middle = characters.Length / 2;
        characters[middle] = characters[middle] == 'A' ? 'B' : 'A';

        return new string(characters);
    }
}
