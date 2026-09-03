using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace VelaCommerce.Infrastructure.DemoLab;

/// <summary>
/// The Demo Lab's shoppers: real HTTP requests, made by this process to itself, over the loopback
/// interface.
/// <para>
/// <b>Why HTTP at all, when the handler is a method call away.</b> Because a lab that reaches past
/// the pipeline proves something narrower than what it claims. The invariants on display are
/// enforced by the composed system - the session cookie that separates two shoppers, the rate
/// limiter, the row quotas, the tenancy filter that the DbContext applies from the request scope,
/// the endpoint, the transaction. Calling an internal method with a hand-made session id would
/// exercise the last two and quietly assume the rest, which is exactly the assumption a reviewer is
/// entitled to disbelieve. Going over the wire also means the transcript can show the wire: the
/// bytes in this file are the bytes on the page.
/// </para>
/// <para>
/// <b>Why one long-lived <see cref="HttpClient"/>, like the outbox's.</b> One destination, resolved
/// per run, on loopback, where there is no DNS to go stale - so the handler cycling that
/// <c>IHttpClientFactory</c> exists to provide buys nothing, and taking the package reference to
/// get it would follow reasoning this repository has already rejected twice. Fifty concurrent
/// requests share this instance's connection pool rather than opening fifty handlers.
/// </para>
/// <para>
/// <b>Why cookies are handled by hand rather than by a <c>CookieContainer</c>.</b> A run needs
/// fifty <em>distinct</em> visitors, and a container is per-client, so automatic cookies would mean
/// fifty clients and fifty handlers. Holding each visitor's sealed cookie as an opaque string and
/// setting the header explicitly gives one client, exact control over who is who, and - the reason
/// that matters most here - a request whose headers are known precisely enough to print.
/// </para>
/// <para>
/// <b>Nothing in this class ever throws for a transport failure.</b> A refused connection, a
/// timeout or a reset is recorded as an exchange with no status, because a run that dies on its
/// third of fifty requests should hand back a transcript showing where it stopped, not a 500 that
/// says nothing about which invariant was being tested.
/// </para>
/// </summary>
public sealed class DemoLabLoopback : IDisposable
{
    /// <summary>
    /// What the transcript prints instead of a session cookie or a Set-Cookie value.
    /// <para>
    /// The cookie IS the visitor's identity - the middleware's own comment calls it a credential -
    /// so printing one in a response that anybody can fetch would hand over the ability to act as
    /// that shopper. The header is still shown, because "this request carried a session cookie" is
    /// a load-bearing fact about how the shop tells two racers apart.
    /// </para>
    /// </summary>
    public const string RedactedCookie = "[redacted: the sealed session cookie is a credential]";

    /// <summary>Response headers worth printing. Everything else is transport noise.</summary>
    private static readonly string[] InterestingResponseHeaders =
        ["Content-Type", "Retry-After", "Cache-Control", "Vary", "Location", "Set-Cookie"];

    private readonly DemoLabOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHandler;

    /// <summary>Builds the client.</summary>
    /// <param name="options">Bounds; only the per-request timeout and body cap are read here.</param>
    /// <param name="handler">
    /// For tests that want to answer without a socket. Production passes nothing and this instance
    /// owns the default handler for its lifetime.
    /// </param>
    public DemoLabLoopback(DemoLabOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _ownsHandler = handler is null;

        // UseCookies = false, and it is the single most important line in this class.
        //
        // The default handler keeps a CookieContainer shared by every request it makes. With fifty
        // shoppers going through one client, that container collects fifty Set-Cookie values for
        // one host, keeps the last, and silently appends it to every subsequent request - so a
        // request carrying shopper 12's cookie arrives with two vela.session values and the server
        // believes whichever it parses last. The result is fifty racers who are sometimes one
        // visitor, which destroys the isolation the whole demonstration rests on AND collapses
        // fifty rate-limit buckets into one. Turning the container off makes the Cookie header this
        // class writes the only one there is.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { UseCookies = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);

        // The real bound is the per-request linked token in SendAsync, which the run's own budget
        // also feeds. This is only a backstop against HttpClient's 100-second default outliving a
        // run that has already given up and torn its fixture down.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Sends one request and records everything the transcript needs to show it.
    /// </summary>
    /// <param name="origin">Where this host is listening. Resolved once per run by the caller.</param>
    /// <param name="request">Method, path, body and the visitor sending it.</param>
    /// <param name="cancellationToken">The run's budget. A per-request timeout is linked onto it.</param>
    /// <returns>The exchange, whether it succeeded or not. Never null, never throws for I/O.</returns>
    public async Task<DemoLabExchange> SendAsync(
        Uri origin,
        DemoLabRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(request);

        var shown = new List<DemoLabHeader>();
        var started = Stopwatch.GetTimestamp();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        try
        {
            using var message = new HttpRequestMessage(request.Method, new Uri(origin, request.Path));

            if (request.Body is { } body)
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType ?? "application/json");
                message.Content = content;
                shown.Add(new DemoLabHeader("Content-Type", content.Headers.ContentType.ToString()));
            }

            if (request.SessionCookie is { Length: > 0 })
            {
                // TryAddWithoutValidation throughout: the sealed cookie is base64url with padding
                // characters HttpClient's cookie parser has opinions about, and the signature header
                // below contains the comma that the header parser would treat as a value separator.
                // Neither value is ours to reformat - they have to arrive exactly as issued.
                message.Headers.TryAddWithoutValidation("Cookie", $"{request.CookieName}={request.SessionCookie}");
                shown.Add(new DemoLabHeader("Cookie", $"{request.CookieName}={RedactedCookie}"));
            }

            foreach (var header in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Name, header.Value);
                shown.Add(header);
            }

            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            return new DemoLabExchange(
                request.Method.Method,
                request.Path,
                shown,
                Describe(request.Body, request.ContentType),
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                ResponseHeaders(response),
                Truncate(text),
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                IssuedSessionCookie(response, request.CookieName),
                Transport: null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return Failed(request, shown, started, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-request timeout fired while the run itself still had budget. Reported as a
            // failed exchange rather than thrown, so the transcript shows which call stalled.
            return Failed(
                request,
                shown,
                started,
                $"No response within {_options.RequestTimeout.TotalSeconds:0.#}s.");
        }
    }

    private DemoLabExchange Failed(
        DemoLabRequest request,
        List<DemoLabHeader> shown,
        long started,
        string reason) =>
        new(
            request.Method.Method,
            request.Path,
            shown,
            Describe(request.Body, request.ContentType),
            StatusCode: 0,
            ReasonPhrase: "no response",
            ResponseHeaders: [],
            ResponseBody: string.Empty,
            ElapsedMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            IssuedSessionCookie: null,
            Transport: reason);

    /// <summary>
    /// Pulls a newly issued session cookie out of a response, so the caller can keep being the
    /// same visitor on its next request.
    /// <para>
    /// Deliberately returned on its own rather than inside the printed headers: this value is the
    /// credential, and the only safe place for it is a local variable in the run. The printed
    /// <c>Set-Cookie</c> is redacted by <see cref="ResponseHeaders"/>.
    /// </para>
    /// </summary>
    private static string? IssuedSessionCookie(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var prefix = cookieName + "=";

        foreach (var value in values)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = value.IndexOf(';', StringComparison.Ordinal);
            var end = attributes < 0 ? value.Length : attributes;

            return value[prefix.Length..end];
        }

        return null;
    }

    private static IReadOnlyList<DemoLabHeader> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new List<DemoLabHeader>();

        foreach (var name in InterestingResponseHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                headers.Add(new DemoLabHeader(name, Show(name, values)));
            }
            else if (response.Content.Headers.TryGetValues(name, out var contentValues))
            {
                headers.Add(new DemoLabHeader(name, Show(name, contentValues)));
            }
        }

        return headers;

        static string Show(string name, IEnumerable<string> values) =>
            name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                ? RedactedCookie
                : string.Join(", ", values);
    }

    /// <summary>Renders a request body for the transcript, or says why there is nothing to show.</summary>
    private string? Describe(byte[]? body, string? contentType)
    {
        if (body is null)
        {
            return null;
        }

        // The settlement payloads are UTF-8 JSON that was signed as bytes, so decoding them for
        // display is safe and is the whole point - a reader needs to see the event id and the
        // order reference the signature covers.
        return contentType is null || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? Truncate(Encoding.UTF8.GetString(body))
            : $"[{body.Length} bytes of {contentType}]";
    }

    private string Truncate(string text) =>
        text.Length <= _options.MaxBodyCharacters
            ? text
            : text[.._options.MaxBodyCharacters]
              + $"... [truncated: {text.Length - _options.MaxBodyCharacters} more characters]";

    /// <summary>Disposes the client, and the handler if this instance created it.</summary>
    public void Dispose()
    {
        if (_ownsHandler)
        {
            _http.Dispose();
        }
    }
}

/// <summary>One request the lab is about to make, as the transcript will describe it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">Origin-relative, because the origin is noise and the path is the point.</param>
/// <param name="CookieName">The session cookie's name, taken from the middleware that issues it.</param>
/// <param name="SessionCookie">The sealed cookie identifying the sending visitor, or null for a stranger.</param>
/// <param name="Body">The exact bytes to send. Bytes, not an object: a settlement's signature covers these octets.</param>
/// <param name="ContentType">Defaults to <c>application/json</c> when a body is present.</param>
/// <param name="Headers">Extra headers, all of which are printed - so nothing secret belongs here.</param>
public sealed record DemoLabRequest(
    HttpMethod Method,
    string Path,
    string CookieName,
    string? SessionCookie = null,
    byte[]? Body = null,
    string? ContentType = null,
    IReadOnlyList<DemoLabHeader>? Headers = null)
{
    /// <summary>Extra headers, never null at the point of use.</summary>
    public IReadOnlyList<DemoLabHeader> Headers { get; init; } = Headers ?? [];
}

/// <summary>A header as it will be printed. Values are already display-safe.</summary>
/// <param name="Name">Header name.</param>
/// <param name="Value">Header value, redacted where it is a credential.</param>
public sealed record DemoLabHeader(string Name, string Value);

/// <summary>
/// One completed request and its answer: everything the transcript shows for a single line, plus
/// the one thing it must not show.
/// </summary>
/// <param name="Method">The method sent.</param>
/// <param name="Path">The path requested.</param>
/// <param name="RequestHeaders">Display-safe request headers.</param>
/// <param name="RequestBody">The body sent, decoded and truncated for display.</param>
/// <param name="StatusCode">The status returned, or 0 when nothing answered.</param>
/// <param name="ReasonPhrase">The reason phrase, for a line a person can read.</param>
/// <param name="ResponseHeaders">Display-safe response headers.</param>
/// <param name="ResponseBody">The response body, truncated for display.</param>
/// <param name="ElapsedMilliseconds">Wall time the caller waited, including reading the body.</param>
/// <param name="IssuedSessionCookie">
/// A newly minted session cookie, for the run's own use. Never mapped onto the wire.
/// </param>
/// <param name="Transport">Why there is no response, when there is none. Null on a real answer.</param>
public sealed record DemoLabExchange(
    string Method,
    string Path,
    IReadOnlyList<DemoLabHeader> RequestHeaders,
    string? RequestBody,
    int StatusCode,
    string ReasonPhrase,
    IReadOnlyList<DemoLabHeader> ResponseHeaders,
    string ResponseBody,
    long ElapsedMilliseconds,
    string? IssuedSessionCookie,
    string? Transport)
{
    /// <summary>Whether the shop answered at all.</summary>
    public bool Answered => StatusCode > 0;
}
