using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace VelaCommerce.Storefront.Cart;

/// <summary>
/// The storefront's one route to the API. Five cart calls and a variant lookup, and nothing else.
/// <para>
/// <strong>It is the only type in this application that talks to something that can be asleep.</strong>
/// The catalog is a static file and every browse, search, filter and sort is answered from memory;
/// this client is reached when a shopper adds something or opens the drawer, never on first paint.
/// Keeping that boundary in one class is what makes it checkable: if a component ends up injecting
/// this into <c>OnInitializedAsync</c>, the storefront has stopped working with the API switched
/// off, and the fix is at the call site rather than in here.
/// </para>
/// <para>
/// The client is deliberately dumb about failure: it makes one attempt and translates what came
/// back into a <see cref="CartApiException"/> that says whether trying again could plausibly help.
/// Deadlines, retries and the "waking the shop up" state belong to <c>CartState</c>, which is the
/// thing that has states to be in.
/// </para>
/// </summary>
public sealed class CartApiClient
{
    /// <summary>The cart resource. Relative, so it resolves against the app's own origin — which is now also the API's.</summary>
    private const string CartPath = "api/cart";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client over the storefront's own <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="http">
    /// The app-base-address client. This is not a second client pointed at some other host: the
    /// whole point of serving the storefront from the API is that "the app's origin" and "the API's
    /// origin" are the same string, so the session cookie is a first-party cookie and rides along.
    /// </param>
    public CartApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// Reads the visitor's cart. Always 200 with a body — a visitor who has never added anything
    /// gets an empty cart rather than a 404 — and reading never creates a cart row, so this call is
    /// free to make when the drawer opens.
    /// </summary>
    public Task<CartDocument> GetCartAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, CartPath, content: null, cancellationToken);

    /// <summary>
    /// Adds a variant. The quantity is an increment merged into any existing line, which is exactly
    /// why this is the one call in the class that must never be retried blind.
    /// </summary>
    public Task<CartDocument> AddItemAsync(Guid variantId, int quantity, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            $"{CartPath}/items",
            JsonContent.Create(new AddCartItemBody(variantId, quantity), CartApiJsonContext.Default.AddCartItemBody),
            cancellationToken);

    /// <summary>
    /// Sets a line's quantity. Absolute, not a delta: sending "3" twice ends at three, which makes
    /// the call safe to repeat after a dropped response.
    /// </summary>
    public Task<CartDocument> SetQuantityAsync(Guid variantId, int quantity, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Patch,
            $"{CartPath}/items/{variantId}",
            JsonContent.Create(new ChangeQuantityBody(quantity), CartApiJsonContext.Default.ChangeQuantityBody),
            cancellationToken);

    /// <summary>Removes a line. Idempotent by design: removing one that is already gone is a 200, not a 404.</summary>
    public Task<CartDocument> RemoveItemAsync(Guid variantId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"{CartPath}/items/{variantId}", content: null, cancellationToken);

    /// <summary>Empties the cart. Idempotent, and never creates a cart row.</summary>
    public Task<CartDocument> ClearAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, CartPath, content: null, cancellationToken);

    /// <summary>
    /// Resolves a product's SKUs to the variant ids the cart endpoints address lines by.
    /// <para>
    /// A read, so it is safe to retry, and in practice it doubles as a warm-up: on a cold start this
    /// is usually the call that pays for the container waking, leaving the add that follows it to
    /// run against a warm API.
    /// </para>
    /// </summary>
    /// <returns>SKU to variant id for every live variant of the product.</returns>
    public async Task<IReadOnlyDictionary<string, Guid>> GetVariantIdsAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(HttpMethod.Get, $"api/catalog/products/{Uri.EscapeDataString(slug)}", content: null);
        using var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);

        var document = await ReadAsync(response, CartApiJsonContext.Default.ProductVariantsDocument, cancellationToken)
            .ConfigureAwait(false);

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var variant in document.Variants ?? [])
        {
            if (variant.Sku.Length > 0 && variant.Id != Guid.Empty)
            {
                ids[variant.Sku] = variant.Id;
            }
        }

        return ids;
    }

    private async Task<CartDocument> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(method, path, content);
        using var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(response, CartApiJsonContext.Default.CartDocument, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the request, and states the two browser-level decisions the cart depends on rather
    /// than inheriting them.
    /// <para>
    /// <strong>Credentials.</strong> <c>fetch</c> defaults to <c>same-origin</c>, which would in fact
    /// be enough now that the storefront is served by the API host — but "enough by default" is
    /// exactly the kind of assumption that turns into a silently empty cart the day somebody puts
    /// the storefront on its own domain. Saying <c>Include</c> makes the requirement visible at the
    /// place it is required: this request only works because it carries the session cookie.
    /// </para>
    /// <para>
    /// <strong>Cache.</strong> The API already answers cart reads with <c>no-store</c>, so this is
    /// belt to that's braces — but a cart served from the browser's own HTTP cache would show a
    /// shopper a total they have already changed, and that is not a bug worth trusting one header to
    /// prevent.
    /// </para>
    /// </summary>
    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };

        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);

        return request;
    }

    /// <summary>
    /// Sends the request and turns anything that is not a success into a
    /// <see cref="CartApiException"/> the drawer can put on screen.
    /// </summary>
    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline the caller set, not a failure of the server. Reported as retryable
            // because a cold start is the overwhelmingly likely cause and a second attempt against a
            // now-warm container usually succeeds.
            throw new CartApiException(
                "The shop is taking longer than usual to answer.",
                "The request passed its deadline. The API scales to zero, so the first call after a "
                + "quiet period has to wait for it to start.",
                statusCode: null,
                retryable: true);
        }
        catch (HttpRequestException exception)
        {
            throw new CartApiException(
                "The shop could not be reached.",
                $"The request to {request.RequestUri} failed before a response arrived: {exception.Message}",
                exception.StatusCode,
                retryable: true);
        }
        finally
        {
            request.Dispose();
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var problem = await TryReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            var status = response.StatusCode;

            throw new CartApiException(
                problem?.Title is { Length: > 0 } title ? title : DescribeStatus(status),
                problem?.Detail,
                status,
                IsRetryable(status));
        }
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var value = await response.Content
                .ReadFromJsonAsync(typeInfo, cancellationToken)
                .ConfigureAwait(false);

            return value ?? throw new CartApiException(
                "The shop sent back an answer this storefront could not read.",
                "The response body was empty or JSON null where a document was expected.",
                response.StatusCode,
                retryable: false);
        }
        catch (JsonException exception)
        {
            // Almost always a proxy or a captive portal answering with HTML, or an API from a
            // different build. Not retryable: the same request would produce the same soup.
            throw new CartApiException(
                "The shop sent back an answer this storefront could not read.",
                $"The response was not the JSON this build expects: {exception.Message}",
                response.StatusCode,
                retryable: false);
        }
    }

    private static async Task<ApiProblem?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync(CartApiJsonContext.Default.ApiProblem, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or NotSupportedException)
        {
            // A failure body that is not problem details is not itself an error worth reporting; the
            // status code alone still produces a usable message.
            return null;
        }
    }

    /// <summary>
    /// Which failures are worth a second attempt.
    /// <para>
    /// Only the ones that describe a server that is not ready yet. A 400 or a 404 is a decision the
    /// API has made about this exact request and would make again, and retrying it would turn one
    /// clear refusal into three identical ones and a longer wait. The 500 from a host composed
    /// without the session middleware is in the same category: deterministic, and not the shopper's
    /// to wait out.
    /// </para>
    /// </summary>
    private static bool IsRetryable(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        _ => false,
    };

    private static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "That item is no longer in the catalog.",
        HttpStatusCode.BadRequest => "The shop would not accept that change to the cart.",
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            "The shop is still waking up.",
        _ => "The shop could not complete that request.",
    };
}

/// <summary>
/// A cart call that did not succeed, described in terms a shopper can act on.
/// <para>
/// Two messages, because they have two audiences and conflating them serves neither.
/// <see cref="Message"/> goes on screen and says what happened in the shop's own language;
/// <see cref="Detail"/> is the technical line, shown in a disclosure or a console, that says which
/// request failed and how. The dishonest alternative is showing a shopper a status code, which tells
/// them nothing, or showing a developer "something went wrong", which tells them less.
/// </para>
/// </summary>
public sealed class CartApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The shopper-facing sentence.</param>
    /// <param name="detail">The technical explanation, or null when the message is already the whole story.</param>
    /// <param name="statusCode">The HTTP status, or null when no response arrived at all.</param>
    /// <param name="retryable">Whether repeating the same request could plausibly succeed.</param>
    public CartApiException(string message, string? detail, HttpStatusCode? statusCode, bool retryable)
        : base(message)
    {
        Detail = detail;
        StatusCode = statusCode;
        Retryable = retryable;
    }

    /// <summary>The technical explanation behind <see cref="Exception.Message"/>. Null when there is nothing to add.</summary>
    public string? Detail { get; }

    /// <summary>The status the API answered with, or null when the request never got a response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Whether the caller may sensibly try again. False for anything the API decided about this
    /// request specifically — a rejected quantity, a withdrawn variant — because those answers do
    /// not change with time.
    /// </summary>
    public bool Retryable { get; }
}
