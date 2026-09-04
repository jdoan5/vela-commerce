using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// The two calls that turn a cart into an order and then watch it move: <c>POST /api/checkout</c>
/// and <c>GET /api/orders/{orderNumber}</c>.
/// <para>
/// <strong>Neither is ever reached on a first paint.</strong> The catalog is a static file and every
/// browse, search, filter and sort is answered from memory; this client is touched when a shopper
/// presses Place order, and when they are standing on the order page watching the timeline. Keeping
/// that boundary in one small class is what makes it checkable — the same discipline
/// <c>CartApiClient</c> holds for the cart.
/// </para>
/// <para>
/// <strong>The client makes exactly one attempt and never sleeps.</strong> Deadlines, retries and
/// the decision about whether a key may be reused belong to the page, which is the thing with a
/// shopper watching it. What this class does own is the translation from HTTP into
/// <see cref="CheckoutOutcome"/>, because that mapping is the API's contract and must exist in one
/// place rather than in an <c>if</c> ladder inside a Razor file.
/// </para>
/// </summary>
public sealed class CheckoutApiClient
{
    /// <summary>The conventional place for the key. Sent here <em>and</em> in the body; the API permits both when they agree.</summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client over the storefront's own <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="http">
    /// The app-base-address client. Not a second client aimed at some other host: the whole point of
    /// serving the storefront from the API is that the app's origin and the API's origin are the
    /// same string, so the <c>HttpOnly; SameSite=Lax</c> session cookie is a first-party cookie and
    /// rides along. A cross-origin build of this page would checkout into a stranger's empty cart.
    /// </param>
    public CheckoutApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// Places the order the visitor's session cart describes, and reports which of the endpoint's
    /// documented answers came back.
    /// <para>
    /// This method does not throw for a refusal. A declined card, a moved price and a lost race are
    /// business answers rather than exceptions — that is the API's stated position, and a client
    /// that turned them back into exceptions would be arguing with it. It does not throw for a
    /// timeout either: an interrupted checkout is the single most important case on this screen and
    /// it deserves a named outcome, not a catch block at the call site.
    /// </para>
    /// </summary>
    /// <param name="body">Address, key and scenario. Everything else — lines, prices, totals — is the server's.</param>
    /// <param name="cancellationToken">The caller's deadline. Cancellation is reported as <see cref="CheckoutOutcome.Interrupted"/>, not thrown.</param>
    public async Task<CheckoutResult> PlaceOrderAsync(PlaceOrderBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var request = BuildRequest(HttpMethod.Post, "api/checkout");
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, body.IdempotencyKey);
        request.Content = JsonContent.Create(body, CheckoutApiJsonContext.Default.PlaceOrderBody);

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Interrupted(
                "The request passed its deadline before the shop answered. The API scales to zero, "
                + "so the first call after a quiet period waits for a container to start.");
        }
        catch (HttpRequestException exception)
        {
            return Interrupted($"The request to {request.RequestUri} failed before a response arrived: {exception.Message}");
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            // The three successes. 200, 201 and 202 mean three genuinely different things and are
            // kept apart all the way to the screen: created and paid, created and settling, and
            // "you already did this, here is the same order".
            if (response.IsSuccessStatusCode)
            {
                var order = await TryReadAsync(
                        response,
                        CheckoutApiJsonContext.Default.OrderDocument,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (order is null)
                {
                    // A 2xx whose body cannot be read is the worst answer of the lot: something was
                    // almost certainly created and this build cannot say what. Reported as
                    // interrupted so the retry path — same key — runs and fetches it back.
                    return Interrupted(
                        $"The shop answered {(int)response.StatusCode} but the body was not the JSON this build expects.");
                }

                return new CheckoutResult(
                    response.StatusCode switch
                    {
                        HttpStatusCode.Created => CheckoutOutcome.Placed,
                        HttpStatusCode.Accepted => CheckoutOutcome.Settling,
                        _ => CheckoutOutcome.AlreadyPlaced,
                    },
                    order,
                    Problem: null,
                    Detail: null);
            }

            var problem = await TryReadAsync(
                    response,
                    CheckoutApiJsonContext.Default.CheckoutProblem,
                    cancellationToken)
                .ConfigureAwait(false);

            return new CheckoutResult(Classify(response.StatusCode, problem), Order: null, problem, Detail: null);
        }
    }

    /// <summary>
    /// Reads an order by number, optionally with the signed retrieval token.
    /// <para>
    /// Two ways in, and the token is the interesting one: it opens the order for whoever holds the
    /// link, in any browser, with or without a session. That is what makes a confirmation link
    /// forwardable and what lets the order page work after a cleared cookie. With no token the
    /// endpoint falls back to "does this order belong to the calling session", which is the ordinary
    /// path straight after a checkout in the same tab.
    /// </para>
    /// </summary>
    /// <exception cref="OrderApiException">
    /// Thrown for every failure, because unlike a checkout there is no partial success to report:
    /// either there is an order to render or there is not. <see cref="OrderApiException.NotFound"/>
    /// separates "no such order, and never will be" from "could not reach the shop", which the page
    /// must word very differently.
    /// </exception>
    public async Task<OrderDocument> GetOrderAsync(
        string orderNumber,
        string? token,
        CancellationToken cancellationToken)
    {
        var path = $"api/orders/{Uri.EscapeDataString(orderNumber)}";

        if (!string.IsNullOrEmpty(token))
        {
            path += $"?token={Uri.EscapeDataString(token)}";
        }

        var request = BuildRequest(HttpMethod.Get, path);
        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OrderApiException(
                "The shop is taking longer than usual to answer.",
                "The request passed its deadline. The API scales to zero, so the first call after a "
                + "quiet period has to wait for it to start.",
                notFound: false,
                retryable: true);
        }
        catch (HttpRequestException exception)
        {
            throw new OrderApiException(
                "The shop could not be reached.",
                $"The request to {request.RequestUri} failed before a response arrived: {exception.Message}",
                notFound: false,
                retryable: true);
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var order = await TryReadAsync(
                        response,
                        CheckoutApiJsonContext.Default.OrderDocument,
                        cancellationToken)
                    .ConfigureAwait(false);

                return order ?? throw new OrderApiException(
                    "The shop sent back an answer this storefront could not read.",
                    "The response was not the JSON this build expects.",
                    notFound: false,
                    retryable: false);
            }

            // Every way of failing to reach an order is a 404 by design — a malformed number, an
            // expired token, someone else's order — so that the difference between two status codes
            // cannot be used to discover which order numbers exist. The page therefore says "this
            // link does not open an order" rather than guessing which of the three happened.
            var problem = await TryReadAsync(
                    response,
                    CheckoutApiJsonContext.Default.CheckoutProblem,
                    cancellationToken)
                .ConfigureAwait(false);

            var notFound = response.StatusCode == HttpStatusCode.NotFound;

            throw new OrderApiException(
                problem?.Title is { Length: > 0 } title
                    ? title
                    : notFound ? "No such order" : "The shop could not answer for that order.",
                problem?.Detail,
                notFound,
                retryable: !notFound);
        }
    }

    /// <summary>
    /// Asks for the whole outstanding balance back.
    /// <para>
    /// No amount is sent. The server refunds what is left, and refuses an overshoot rather than
    /// clamping it — so a client that computed the remainder itself would eventually be a cent out
    /// and would turn a refund into a 409 for no reason a shopper could understand.
    /// </para>
    /// </summary>
    /// <param name="orderNumber">The order to refund.</param>
    /// <param name="idempotencyKey">
    /// This attempt's key. The caller must reuse it across a retry of the same intent: that is the
    /// difference between asking again and refunding twice, and the server enforces it.
    /// </param>
    /// <param name="cancellationToken">The caller's deadline.</param>
    public Task<RefundResultDocument> RefundAsync(
        string orderNumber,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        PostMoneyAsync(
            $"api/orders/{Uri.EscapeDataString(orderNumber)}/refunds",
            idempotencyKey,
            JsonContent.Create(
                new RefundBody(Amount: null, idempotencyKey, ScenarioHint: null),
                CheckoutApiJsonContext.Default.RefundBody),
            cancellationToken);

    /// <summary>
    /// Cancels the order, returning anything it has already taken.
    /// <para>
    /// One call, because the server treats the two as one act: an order cannot be left cancelled
    /// with money still on it, and it cannot be refunded and left open.
    /// </para>
    /// </summary>
    public Task<RefundResultDocument> CancelOrderAsync(
        string orderNumber,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        PostMoneyAsync(
            $"api/orders/{Uri.EscapeDataString(orderNumber)}/cancellation",
            idempotencyKey,
            JsonContent.Create(
                new CancelOrderBody(idempotencyKey, ScenarioHint: null),
                CheckoutApiJsonContext.Default.CancelOrderBody),
            cancellationToken);

    /// <summary>
    /// The shared half of both money-moving calls.
    /// <para>
    /// These throw on a refusal, unlike <see cref="PlaceOrderAsync"/>, and the difference is not an
    /// inconsistency. A checkout has four documented business answers a screen must render
    /// differently; a refund has one success and a sentence explaining why not, and the server
    /// already writes that sentence. Inventing an outcome enum to carry a string would be ceremony.
    /// </para>
    /// </summary>
    /// <exception cref="OrderApiException">On any non-2xx, timeout or transport failure.</exception>
    private async Task<RefundResultDocument> PostMoneyAsync(
        string path,
        string idempotencyKey,
        HttpContent body,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);
        request.Content = body;

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OrderApiException(
                "The shop is taking longer than usual to answer.",
                "The request passed its deadline. Nothing is lost: trying again with the same key "
                + "cannot move the money twice, which is what the key is for.",
                notFound: false,
                retryable: true);
        }
        catch (HttpRequestException exception)
        {
            throw new OrderApiException(
                "The shop could not be reached.",
                $"The request to {request.RequestUri} failed before a response arrived: {exception.Message}. "
                + "Trying again with the same key is safe.",
                notFound: false,
                retryable: true);
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var result = await TryReadAsync(
                        response,
                        CheckoutApiJsonContext.Default.RefundResultDocument,
                        cancellationToken)
                    .ConfigureAwait(false);

                return result ?? throw new OrderApiException(
                    "The shop sent back an answer this storefront could not read.",
                    "The response was not the JSON this build expects.",
                    notFound: false,
                    retryable: false);
            }

            var problem = await TryReadAsync(
                    response,
                    CheckoutApiJsonContext.Default.CheckoutProblem,
                    cancellationToken)
                .ConfigureAwait(false);

            var notFound = response.StatusCode == HttpStatusCode.NotFound;

            throw new OrderApiException(
                problem?.Title is { Length: > 0 } title ? title : "The shop could not do that.",
                problem?.Detail,
                notFound,
                // A 409 is the world disagreeing, not a fault: repeating it changes nothing. A 502
                // is worth retrying, and the same key makes retrying safe.
                retryable: response.StatusCode is HttpStatusCode.BadGateway);
        }
    }

    /// <summary>
    /// Maps a refusal onto the outcome the page renders.
    /// <para>
    /// The two 409s are told apart by which extension the server sent, not by parsing the title:
    /// <c>shortfall</c> means stock, <c>priceChanges</c> means money, and the API never sends both.
    /// The two 402s are told apart by the gateway's own <c>outcome</c> — a decline releases the
    /// stock, an abandonment leaves it reserved, and the shopper is entitled to know which.
    /// </para>
    /// </summary>
    private static CheckoutOutcome Classify(HttpStatusCode status, CheckoutProblem? problem) => status switch
    {
        HttpStatusCode.PaymentRequired =>
            string.Equals(problem?.Payment?.Outcome, "Declined", StringComparison.OrdinalIgnoreCase)
                ? CheckoutOutcome.Declined
                : CheckoutOutcome.NotCompleted,

        HttpStatusCode.Conflict =>
            problem?.Shortfall is not null ? CheckoutOutcome.OutOfStock : CheckoutOutcome.PriceMoved,

        HttpStatusCode.BadRequest => CheckoutOutcome.Rejected,

        // 502 and 500 both mean an order may exist and its fate is unknown. They belong with the
        // timeout, not with the refusals: the answer is "try again with the same key", never
        // "correct something and start over".
        _ => CheckoutOutcome.Interrupted,
    };

    private static CheckoutResult Interrupted(string detail) =>
        new(CheckoutOutcome.Interrupted, Order: null, Problem: null, detail);

    /// <summary>
    /// Reads a body, or returns null rather than throwing.
    /// <para>
    /// Null is a legitimate answer at both call sites and the callers handle it: an unreadable
    /// success becomes an interrupted checkout, an unreadable problem body still leaves a status
    /// code to classify by. A proxy or a captive portal answering with HTML must not be the thing
    /// that takes a payment screen down.
    /// </para>
    /// </summary>
    private static async Task<T?> TryReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the request and states the two browser-level decisions checkout depends on rather than
    /// inheriting them, for the same reasons <c>CartApiClient</c> does.
    /// <para>
    /// <strong>Credentials.</strong> The order is the session's cart made permanent; without the
    /// cookie the server would find an empty cart and refuse. <c>fetch</c> defaults to
    /// <c>same-origin</c>, which is enough today — saying <c>Include</c> puts the requirement at the
    /// place it is required, so a future split onto two domains fails loudly rather than silently.
    /// </para>
    /// <para>
    /// <strong>Cache.</strong> An order response carries a name, an address and a capability token,
    /// and the token sits in a query string, which is the part of a URL caches key on. The API
    /// already answers <c>no-store</c>; this says the browser must not serve one from its own cache
    /// either, which is what keeps a polled timeline from showing a status it has outgrown.
    /// </para>
    /// </summary>
    private static HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);

        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);

        return request;
    }
}

/// <summary>
/// An order read that did not produce an order.
/// <para>
/// Two messages for two audiences, matching <c>CartApiException</c>: <see cref="Exception.Message"/>
/// goes on screen in the shop's own language, <see cref="Detail"/> is the technical line behind a
/// disclosure. Showing a shopper a status code tells them nothing; showing a developer "something
/// went wrong" tells them less.
/// </para>
/// </summary>
public sealed class OrderApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The shopper-facing sentence.</param>
    /// <param name="detail">The technical explanation, or null when the message is the whole story.</param>
    /// <param name="notFound">True when the API said 404 — which it says for every way of failing to reach an order.</param>
    /// <param name="retryable">Whether repeating the same request could plausibly succeed.</param>
    public OrderApiException(string message, string? detail, bool notFound, bool retryable)
        : base(message)
    {
        Detail = detail;
        NotFound = notFound;
        Retryable = retryable;
    }

    /// <summary>The technical explanation behind <see cref="Exception.Message"/>.</summary>
    public string? Detail { get; }

    /// <summary>
    /// True when the link does not open an order. Deliberately not broken down further: the API
    /// answers a bad number, an expired token and someone else's order identically, on purpose, so
    /// the page must not pretend to know which.
    /// </summary>
    public bool NotFound { get; }

    /// <summary>Whether the same request is worth making again. False for a 404, which will stay a 404.</summary>
    public bool Retryable { get; }
}
