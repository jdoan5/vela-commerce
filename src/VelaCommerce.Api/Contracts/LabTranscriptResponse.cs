namespace VelaCommerce.Api.Contracts;

/// <summary>
/// One line of the transcript: what the lab did, the HTTP it sent, what came back, how long it
/// took, and a sentence saying what just happened.
/// <para>
/// <b><see cref="Fidelity"/> is the field that makes the rest worth reading.</b> A lab that
/// silently simulated a race would be worse than no lab, because it would produce a convincing
/// document about a thing that never happened. So every step states how faithful it is, and the
/// two ways a step can be less than a live exchange - a repeat that was elided, and an outcome
/// that was not followed to its end - are named on the step itself rather than in a footnote
/// somewhere else.
/// </para>
/// </summary>
/// <param name="Number">Position in the transcript, from one.</param>
/// <param name="Title">What this step is, in a few words.</param>
/// <param name="Narration">
/// What just happened, in plain English. The line a reader who does not want to parse a status code
/// should be able to follow the whole run from.
/// </param>
/// <param name="Request">The request as sent, or null for a step that is commentary rather than HTTP.</param>
/// <param name="Response">The answer as received, or null for the same reason.</param>
/// <param name="ElapsedMilliseconds">Wall time the caller waited, body included. Null on a note.</param>
/// <param name="Concurrency">
/// How many requests were in flight together at this moment. Greater than one is the claim that
/// makes a race test mean anything: these requests were released on one gate, not run in a loop.
/// </param>
/// <param name="Represents">
/// How many identical exchanges this one line stands for. One normally; larger when a run made
/// fifty indistinguishable setup calls and the transcript shows one of them. The tally in the
/// verdict is always computed from all of them, never from the shown sample.
/// </param>
/// <param name="Fidelity">
/// <c>genuine</c> - this exact exchange happened, over HTTP, against the live shop.
/// <c>elided</c> - it happened, along with <see cref="Represents"/> - 1 others not printed.
/// <c>not-followed</c> - it happened, but its asynchronous consequence was left for a background
/// worker and this run did not wait to watch it.
/// Nothing in this lab is ever simulated; if that ever changes, the value for it belongs here.
/// </param>
/// <param name="FidelityNote">The reason, when <see cref="Fidelity"/> is not <c>genuine</c>.</param>
public sealed record LabStepResponse(
    int Number,
    string Title,
    string Narration,
    LabRequestResponse? Request,
    LabResponseResponse? Response,
    long? ElapsedMilliseconds,
    int Concurrency,
    int Represents,
    string Fidelity,
    string? FidelityNote);

/// <summary>The request as it went out.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Origin-relative path.</param>
/// <param name="Headers">
/// The headers worth showing. Session cookies are present but redacted: the fact that a request
/// carried one is load-bearing - it is how the shop tells two racers apart - while the value is a
/// credential that would let a reader act as that shopper.
/// </param>
/// <param name="Body">The body sent, or null.</param>
public sealed record LabRequestResponse(
    string Method,
    string Path,
    IReadOnlyList<LabHeaderResponse> Headers,
    string? Body);

/// <summary>The answer as it came back.</summary>
/// <param name="Status">The status code, or 0 when nothing answered.</param>
/// <param name="Reason">The reason phrase.</param>
/// <param name="Headers">Response headers worth showing, with Set-Cookie redacted.</param>
/// <param name="Body">The body, verbatim, truncated only if it was longer than the transcript cap.</param>
/// <param name="Transport">
/// Why there was no answer, when there was none - a timeout, a reset, a refused connection. Null on
/// a real response. Recorded rather than thrown, so a run that fails halfway still shows where.
/// </param>
public sealed record LabResponseResponse(
    int Status,
    string Reason,
    IReadOnlyList<LabHeaderResponse> Headers,
    string Body,
    string? Transport);

/// <summary>One header, already safe to print.</summary>
/// <param name="Name">Header name.</param>
/// <param name="Value">Header value, redacted where it is a credential.</param>
public sealed record LabHeaderResponse(string Name, string Value);
