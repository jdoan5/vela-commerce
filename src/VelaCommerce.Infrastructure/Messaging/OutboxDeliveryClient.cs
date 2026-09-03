using System.Globalization;
using System.Net.Http.Headers;

using VelaCommerce.Infrastructure.Payments;

namespace VelaCommerce.Infrastructure.Messaging;

/// <summary>
/// Posts one stored message to the receiver. The only place in this system that sends a webhook.
/// <para>
/// <b>It takes bytes, and it takes them for a reason.</b> The signature covers the exact octets the
/// sender hashed, so the single most likely defect in this whole slice is a body that gets
/// rebuilt on its way out — deserialize the payload, hand the object to a JSON-serializing HTTP
/// helper, and the wire now carries different whitespace, a different property order or a
/// different escape under a signature computed over the original. The receiver then reports a
/// signature mismatch, which reads exactly like an attack and is not one. This method therefore
/// cannot be handed an object: its parameter is a byte array, it wraps it in
/// <see cref="ByteArrayContent"/>, and nothing in this namespace references a serializer at all.
/// </para>
/// <para>
/// <b>It never throws for a delivery that failed.</b> Every transport fault comes back as a result,
/// because the caller is inside a database transaction holding a claim on the message: an
/// exception there would roll the claim back and lose the record of the attempt, so one
/// unreachable receiver would produce infinite retries with an attempt count permanently at zero.
/// Cancellation is the one exception that is still allowed through, because a cancelled sweep
/// genuinely should abandon its transaction rather than record a failure that did not happen.
/// </para>
/// <para>
/// <b>One long-lived <see cref="HttpClient"/>, no <c>IHttpClientFactory</c>.</b> The factory earns
/// its keep by cycling handlers so DNS changes are noticed and sockets are shared across many
/// named clients; this client has one destination, resolved once, on loopback, where there is no
/// DNS to go stale. Taking a package reference on <c>Microsoft.Extensions.Http</c> to obtain that
/// would follow the same reasoning <c>PaymentSimulatorOptions</c> already rejected for binding.
/// The handler is injectable so a test can answer without a socket.
/// </para>
/// </summary>
public sealed class OutboxDeliveryClient : IDisposable
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly HttpClient _http;
    private readonly bool _ownsHandler;

    /// <summary>
    /// Builds the client. <paramref name="handler"/> is for tests; production passes nothing and
    /// gets the default handler, which this instance then owns for its lifetime.
    /// </summary>
    public OutboxDeliveryClient(OutboxOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ownsHandler = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // The timeout is also the bound on how long this message's row stays locked, so it is set
        // from configuration rather than left at HttpClient's 100 seconds.
        _http.Timeout = options.DeliveryTimeout;
    }

    /// <summary>
    /// Delivers one payload and reports what happened.
    /// </summary>
    /// <param name="receiver">Where to post. Resolved once at startup, never per message.</param>
    /// <param name="payload">The exact bytes to transmit. Not an object, by design.</param>
    /// <param name="signatureHeader">The stored header value that authenticates those bytes.</param>
    /// <param name="cancellationToken">Cancelled when the host is stopping.</param>
    public async Task<OutboxDeliveryResult> PostAsync(
        Uri receiver,
        byte[] payload,
        string signatureHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, receiver);

        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = Json;

        // TryAddWithoutValidation, not Add. The signature header's value is "t=…,v1=…" and it
        // contains a comma — the character HttpClient treats as the separator between values of a
        // multi-valued header. Validated addition can therefore split one signature into two
        // header values that re-serialize with an inserted space, which changes the header the
        // receiver parses. The value is ours, generated from a timestamp and hex, so there is
        // nothing here that validation would usefully catch anyway.
        request.Headers.TryAddWithoutValidation(PaymentSignature.HeaderName, signatureHeader);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
                return OutboxDeliveryResult.Delivered((int)response.StatusCode);

            // The body is read for the error message only, and only a little of it: a receiver
            // returning a ProblemDetails document says something worth keeping, a receiver
            // returning an HTML error page does not deserve a kilobyte of the outbox table.
            var detail = await ReadShortBodyAsync(response, cancellationToken);

            return OutboxDeliveryResult.Failed(
                (int)response.StatusCode,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The receiver answered {(int)response.StatusCode} {response.ReasonPhrase}. {detail}").Trim());
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Distinguishing it from the
            // host stopping is what lets a slow receiver be recorded as a failed attempt while a
            // shutdown rolls the claim back untouched.
            return OutboxDeliveryResult.Failed(
                statusCode: null,
                $"The receiver did not answer within {_http.Timeout}. ({exception.Message})");
        }
        catch (HttpRequestException exception)
        {
            // Refused connection, DNS, TLS, a receiver that is not up yet. All retryable.
            return OutboxDeliveryResult.Failed(statusCode: null, exception.Message);
        }
    }

    public void Dispose()
    {
        // Disposing the client disposes the handler it created; a handler passed in belongs to
        // whoever passed it, which is why the constructor said disposeHandler: false.
        if (_ownsHandler)
            _http.Dispose();
    }

    private static async Task<string> ReadShortBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int MaxDetail = 200;

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return body.Length <= MaxDetail ? body : body[..MaxDetail];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The status code is the part that matters; a body that could not be read must not
            // turn a recorded failure into an unrecorded one.
            return string.Empty;
        }
    }
}

/// <summary>
/// What one delivery attempt did. A result rather than an exception, so the caller can write the
/// outcome down inside the transaction that claimed the message.
/// </summary>
/// <param name="Success">Whether the receiver answered 2xx.</param>
/// <param name="StatusCode">The status, or null when the request never got an answer at all.</param>
/// <param name="Error">Why it failed. Null on success.</param>
public sealed record OutboxDeliveryResult(bool Success, int? StatusCode, string? Error)
{
    public static OutboxDeliveryResult Delivered(int statusCode) => new(true, statusCode, null);

    public static OutboxDeliveryResult Failed(int? statusCode, string error) => new(false, statusCode, error);
}
