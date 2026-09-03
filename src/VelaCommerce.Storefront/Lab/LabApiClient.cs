using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace VelaCommerce.Storefront.Lab;

/// <summary>
/// The two calls the Demo Lab page makes: <c>GET /api/demo/lab/scenarios</c> and
/// <c>POST /api/demo/lab/run/{scenarioId}</c>.
/// <para>
/// <strong>Neither is ever reached on a first paint, and the lab is not on the first-paint path at
/// all.</strong> The catalog browses, searches and sorts from a static file with the API switched
/// off; this client is touched only by <c>/lab</c>, and only after that page has already painted.
/// A shopper who never opens the lab wakes nothing.
/// </para>
/// <para>
/// <strong>One attempt, no sleeping, no retry loop</strong> — the same discipline
/// <c>CheckoutApiClient</c> holds. A run is up to a hundred and fifty real requests on the server;
/// deciding to ask for that again belongs to the person watching, not to a loop in here.
/// </para>
/// <para>
/// <strong>A refusal is not an exception.</strong> 404 for an unknown permalink, 429 for the
/// published cooldown, 503 for a host that mapped the endpoints without registering the services —
/// all three are answers, and all three carry a sentence the page shows verbatim. Only a run that
/// produced no answer at all is a failure, and it is reported as one named outcome rather than as
/// a catch block at the call site.
/// </para>
/// </summary>
public sealed class LabApiClient
{
    /// <summary>The catalogue path, written once so the page can print it beside the transcript.</summary>
    public const string CatalogPath = "api/demo/lab/scenarios";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client over the storefront's single <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="http">
    /// The app-base-address client. The lab drives real sessions on the server, and the visitor's
    /// own <c>vela.session</c> cookie is what the run endpoint rate-limits against — so this has to
    /// be the same-origin client the rest of the shop uses, not a second one aimed elsewhere.
    /// </param>
    public LabApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// Reads the menu: what each button will do, what it costs, and what the endpoint refuses.
    /// </summary>
    /// <param name="cancellationToken">The caller's deadline.</param>
    /// <exception cref="LabApiException">
    /// Thrown for every failure. Unlike a run there is no partial success to report: either there
    /// is a list of scenarios to render or the page has nothing to say.
    /// </exception>
    public async Task<LabCatalogDocument> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var request = BuildRequest(HttpMethod.Get, CatalogPath);
        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new LabApiException(
                "The shop is taking longer than usual to answer.",
                "The request passed its deadline. The API scales to zero, so the first call after a "
                + "quiet period waits for a container to start and a database to resume.");
        }
        catch (HttpRequestException exception)
        {
            throw new LabApiException(
                "The shop could not be reached.",
                $"The request to {request.RequestUri} failed before a response arrived: {exception.Message}");
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var problem = await TryReadAsync(
                        response,
                        LabApiJsonContext.Default.LabProblemDocument,
                        cancellationToken)
                    .ConfigureAwait(false);

                throw new LabApiException(
                    problem?.Title is { Length: > 0 } title ? title : "The lab's catalogue is not available.",
                    problem?.Detail
                    ?? $"{CatalogPath} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var catalog = await TryReadAsync(
                    response,
                    LabApiJsonContext.Default.LabCatalogDocument,
                    cancellationToken)
                .ConfigureAwait(false);

            return catalog ?? throw new LabApiException(
                "The shop sent back a catalogue this storefront could not read.",
                "The response was not the JSON this build expects. That normally means the "
                + "storefront and the API were built from different commits.");
        }
    }

    /// <summary>
    /// Runs one scenario and brings back its transcript, or the refusal that stopped it.
    /// </summary>
    /// <param name="scenarioId">The scenario's id, straight from the permalink.</param>
    /// <param name="participants">
    /// An optional smaller shopper count. Null asks for the number the claim names, which is what
    /// the page always sends: a fifty-way race demonstrated with eight is a different, weaker claim,
    /// and the server flags it as a caveat when it happens.
    /// </param>
    /// <param name="cancellationToken">The caller's deadline. Cancellation is an outcome, not a throw.</param>
    public async Task<LabRunResult> RunAsync(
        string scenarioId,
        int? participants,
        CancellationToken cancellationToken)
    {
        var path = $"api/demo/lab/run/{Uri.EscapeDataString(scenarioId)}";

        if (participants is int count)
        {
            path += $"?participants={count}";
        }

        var request = BuildRequest(HttpMethod.Post, path);
        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LabRunResult.Unreachable(
                "The run passed this page's deadline before the shop answered. The server abandons a "
                + "run on its own budget and tears its fixture down either way, so nothing is left "
                + "half-finished — but this page never saw the transcript.");
        }
        catch (HttpRequestException exception)
        {
            return LabRunResult.Unreachable(
                $"The request to {request.RequestUri} failed before a response arrived: {exception.Message}");
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var run = await TryReadAsync(
                        response,
                        LabApiJsonContext.Default.LabRunDocument,
                        cancellationToken)
                    .ConfigureAwait(false);

                // A 200 whose body will not parse is the one success that has to be reported as a
                // failure: the run happened on the server and this build cannot show what it did.
                return run is null
                    ? LabRunResult.Unreachable(
                        "The shop answered 200 but the body was not the JSON this build expects. The "
                        + "run itself ran; this page cannot render it.")
                    : new LabRunResult(LabOutcome.Completed, run, Problem: null, (int)response.StatusCode, null, null);
            }

            var problem = await TryReadAsync(
                    response,
                    LabApiJsonContext.Default.LabProblemDocument,
                    cancellationToken)
                .ConfigureAwait(false);

            // Retry-After is the endpoint's own answer to "when may I press this again", and it is
            // read rather than guessed so the countdown on screen matches the throttle exactly.
            int? retryAfter = response.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 } delta
                ? (int)Math.Ceiling(delta.TotalSeconds)
                : null;

            return new LabRunResult(
                LabOutcome.Refused,
                Run: null,
                problem,
                (int)response.StatusCode,
                retryAfter,
                Detail: null);
        }
    }

    /// <summary>
    /// Reads a body, or returns null rather than throwing.
    /// <para>
    /// Null is a legitimate answer at every call site above and each one handles it. A proxy or a
    /// captive portal answering with HTML must not be the thing that takes the page down.
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
    /// Builds the request and states the two browser-level decisions the lab depends on, for the
    /// same reasons <c>CheckoutApiClient</c> does.
    /// <para>
    /// <strong>Credentials.</strong> The run endpoint rate-limits by visitor, and the visitor is the
    /// sealed <c>vela.session</c> cookie. Without it the endpoint has no bucket to charge. Saying
    /// <c>Include</c> rather than relying on the <c>same-origin</c> default puts the requirement
    /// where it is required, so a future split onto two domains fails loudly.
    /// </para>
    /// <para>
    /// <strong>Cache.</strong> A transcript describes one run by one visitor and the endpoint
    /// already answers <c>no-store</c>. This says the browser must not serve one from its own cache
    /// either, so pressing Run twice can never repaint the first run's evidence.
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

/// <summary>How a run ended, from the page's point of view.</summary>
public enum LabOutcome
{
    /// <summary>The scenario ran and there is a transcript to read. The verdict inside may still say the invariant did not hold.</summary>
    Completed,

    /// <summary>The endpoint declined to start it and said why — an unknown id, the cooldown, or a host that is not composed.</summary>
    Refused,

    /// <summary>No answer arrived. The only genuine error state on the page, and the only one that offers a retry.</summary>
    Unreachable,
}

/// <summary>
/// One press of a Run button, in the three shapes it can come back in.
/// </summary>
/// <param name="Outcome">Which of the three happened.</param>
/// <param name="Run">The transcript, on a completed run.</param>
/// <param name="Problem">The endpoint's own words, on a refusal.</param>
/// <param name="Status">The status code, or 0 when nothing answered.</param>
/// <param name="RetryAfterSeconds">The endpoint's <c>Retry-After</c>, on a 429.</param>
/// <param name="Detail">The technical line, on an unreachable run.</param>
public sealed record LabRunResult(
    LabOutcome Outcome,
    LabRunDocument? Run,
    LabProblemDocument? Problem,
    int Status,
    int? RetryAfterSeconds,
    string? Detail)
{
    /// <summary>Builds the no-answer case.</summary>
    /// <param name="detail">What went wrong, for the disclosure under the message.</param>
    public static LabRunResult Unreachable(string detail) =>
        new(LabOutcome.Unreachable, Run: null, Problem: null, Status: 0, RetryAfterSeconds: null, detail);

    /// <summary>True when this refusal is the published per-visitor cooldown rather than a fault.</summary>
    public bool IsCooldown => Outcome == LabOutcome.Refused && Status == (int)HttpStatusCode.TooManyRequests;

    /// <summary>True when the host mapped the lab's endpoints but never registered its services.</summary>
    public bool IsNotComposed => Outcome == LabOutcome.Refused && Status == (int)HttpStatusCode.ServiceUnavailable;

    /// <summary>True when the permalink names a scenario that does not exist.</summary>
    public bool IsUnknownScenario => Outcome == LabOutcome.Refused && Status == (int)HttpStatusCode.NotFound;
}

/// <summary>
/// The lab's catalogue could not be read.
/// <para>
/// Two messages for two audiences, matching <c>OrderApiException</c>: <see cref="Exception.Message"/>
/// goes on screen in the shop's own language, <see cref="Detail"/> is the technical line behind a
/// disclosure.
/// </para>
/// </summary>
public sealed class LabApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reader-facing sentence.</param>
    /// <param name="detail">The technical explanation behind it.</param>
    public LabApiException(string message, string? detail)
        : base(message) => Detail = detail;

    /// <summary>The technical explanation behind <see cref="Exception.Message"/>.</summary>
    public string? Detail { get; }
}
