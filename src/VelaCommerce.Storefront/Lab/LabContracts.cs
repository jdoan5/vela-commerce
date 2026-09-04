using System.Text.Json.Serialization;

namespace VelaCommerce.Storefront.Lab;

/// <summary>
/// The Demo Lab's menu, exactly as <c>GET /api/demo/lab/scenarios</c> answers it.
/// <para>
/// <strong>This is fetched rather than restated, and that is deliberate.</strong> The claim, the
/// invariant, the enforcing statement and the test file are the server's copy —
/// <c>DemoLabScenarioCatalog</c> — and the whole point of the lab is that a button's label and
/// what the button does cannot disagree. The cart's payment-scenario picker duplicates its table
/// because it has to render while the API is asleep; this page has no such excuse, because a
/// scenario cannot be <em>run</em> against a sleeping API either. So nothing here is hardcoded,
/// and a scenario added on the server appears on this page with no storefront change at all.
/// </para>
/// </summary>
/// <param name="Scenarios">Every runnable scenario, in the order the server wants them shown.</param>
/// <param name="Limits">What a public, unauthenticated run endpoint refuses to do.</param>
/// <param name="PaymentScenarios">The simulated gateway's six behaviours.</param>
/// <param name="StockPolicy">Where the units a run races for come from.</param>
public sealed record LabCatalogDocument(
    List<LabScenarioDocument>? Scenarios,
    LabLimitsDocument? Limits,
    List<LabPaymentScenarioDocument>? PaymentScenarios,
    string? StockPolicy)
{
    /// <summary>The scenarios, never null, so a malformed body renders an empty page rather than throwing.</summary>
    public IReadOnlyList<LabScenarioDocument> All => Scenarios ?? [];

    /// <summary>The payment table, never null.</summary>
    public IReadOnlyList<LabPaymentScenarioDocument> Payments => PaymentScenarios ?? [];

    /// <summary>Finds one scenario by its permalink id, matched the way the server matches it.</summary>
    /// <param name="id">The route segment, which may have been through a chat client or a CV.</param>
    public LabScenarioDocument? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(scenario =>
                string.Equals(scenario.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>One scenario, described well enough to judge before pressing anything.</summary>
/// <param name="Id">Stable id, and the last segment of this page's permalink.</param>
/// <param name="Title">The button's label.</param>
/// <param name="Claim">The commercial promise, in a shopper's words.</param>
/// <param name="Invariant">The rule that has to hold for the claim to be true.</param>
/// <param name="Mechanism">What actually enforces it: a statement, an index, a constraint.</param>
/// <param name="ProvenBy">Repository-relative path of the test that proves the same thing in CI.</param>
/// <param name="ProvenByTest">The test method inside that file.</param>
/// <param name="Participants">Simultaneous shoppers one run creates — the load, stated up front.</param>
/// <param name="Units">Units of private fixture stock the run seeds for itself.</param>
/// <param name="Creates">Every row the run writes.</param>
/// <param name="Fidelity">Which parts are genuine. Rendered verbatim; never summarised away.</param>
/// <param name="RunPath">The path the run is POSTed to, printed on screen so it can be curled.</param>
public sealed record LabScenarioDocument(
    string? Id,
    string? Title,
    string? Claim,
    string? Invariant,
    string? Mechanism,
    string? ProvenBy,
    string? ProvenByTest,
    int Participants,
    int Units,
    string? Creates,
    string? Fidelity,
    string? RunPath)
{
    /// <summary>The id, or an empty string. Used as a dictionary key and a route segment.</summary>
    public string Key => Id ?? "";

    /// <summary>The heading to show, falling back to the id so a partial body still reads as something.</summary>
    public string Heading => Title is { Length: > 0 } title ? title : Key;
}

/// <summary>
/// What the lab will not let a caller do, published by the endpoint rather than assumed here.
/// <para>
/// The page reads these instead of hardcoding them because they are configuration: a deployment
/// that lowers the participant cap or lengthens the cooldown would otherwise be described wrongly
/// by a storefront that had shipped months earlier. <see cref="CooldownSeconds"/> in particular is
/// used to disable the Run buttons for exactly as long as the server would refuse them, so a
/// reviewer meets a countdown rather than a 429.
/// </para>
/// </summary>
/// <param name="Enabled">Whether runs are accepted at all on this deployment.</param>
/// <param name="MaxParticipants">Ceiling on simultaneous shoppers in one run.</param>
/// <param name="MaxConcurrentRuns">How many runs may exist at once, across every visitor.</param>
/// <param name="CooldownSeconds">How long one visitor waits between runs.</param>
/// <param name="RunTimeoutSeconds">The server's wall-clock budget for a run.</param>
/// <param name="Policy">Why those numbers.</param>
public sealed record LabLimitsDocument(
    bool Enabled,
    int MaxParticipants,
    int MaxConcurrentRuns,
    int CooldownSeconds,
    int RunTimeoutSeconds,
    string? Policy);

/// <summary>One simulated-gateway behaviour, as the server's own catalogue describes it.</summary>
/// <param name="Scenario">The hint value that selects it.</param>
/// <param name="AmountTrigger">The trailing cents that select it when no hint is given.</param>
/// <param name="AuthorizationResult">What the checkout request itself answers.</param>
/// <param name="Webhooks">What arrives afterwards, and how many times.</param>
/// <param name="Demonstrates">The point of having it.</param>
public sealed record LabPaymentScenarioDocument(
    string? Scenario,
    string? AmountTrigger,
    string? AuthorizationResult,
    string? Webhooks,
    string? Demonstrates);

/// <summary>
/// One run, end to end: the claim, the fixture, every exchange, the rows afterwards, the verdict.
/// </summary>
/// <param name="RunId">This run, for correlating a transcript with a log line.</param>
/// <param name="ScenarioId">Which scenario ran.</param>
/// <param name="Title">Its label.</param>
/// <param name="Claim">The commercial promise being tested.</param>
/// <param name="Invariant">The rule the claim rests on.</param>
/// <param name="Mechanism">What enforces it.</param>
/// <param name="ProvenBy">The test file that proves the same thing in CI.</param>
/// <param name="ProvenByTest">The test method inside it.</param>
/// <param name="StartedAt">When the run began.</param>
/// <param name="ElapsedMilliseconds">Server-side wall time, fixture and teardown included.</param>
/// <param name="Fixture">The private stock this run created to race for.</param>
/// <param name="Steps">The transcript, in order.</param>
/// <param name="Evidence">What the database says, read after the fact.</param>
/// <param name="Verdict">Whether the invariant held, and the checks that decide it.</param>
/// <param name="Caveats">Everything a sceptical reader should know that the steps do not say.</param>
public sealed record LabRunDocument(
    string? RunId,
    string? ScenarioId,
    string? Title,
    string? Claim,
    string? Invariant,
    string? Mechanism,
    string? ProvenBy,
    string? ProvenByTest,
    DateTimeOffset StartedAt,
    long ElapsedMilliseconds,
    LabFixtureDocument? Fixture,
    List<LabStepDocument>? Steps,
    LabEvidenceDocument? Evidence,
    LabVerdictDocument? Verdict,
    List<string>? Caveats)
{
    /// <summary>The transcript, never null.</summary>
    public IReadOnlyList<LabStepDocument> Transcript => Steps ?? [];

    /// <summary>The caveats, never null. Rendered in full and never behind a disclosure.</summary>
    public IReadOnlyList<string> Notes => Caveats ?? [];

    /// <summary>
    /// True when any step is anything other than a live exchange printed as it happened.
    /// <para>
    /// The page uses this to decide whether to put the fidelity summary at the <em>top</em> of the
    /// result rather than only on the steps themselves. A reader who scrolls past the verdict and
    /// stops must not be able to miss that part of what they are looking at was elided.
    /// </para>
    /// </summary>
    public bool HasQualifiedSteps => Transcript.Any(step => !step.IsGenuine);

    /// <summary>
    /// True when a step claims to be simulated — a value the current server never emits.
    /// <para>
    /// Handled anyway, and handled loudly. A lab that quietly rendered a simulated step as though
    /// it were a live one would be producing a convincing document about something that did not
    /// happen, which is worse than having no lab. If this ever becomes true the page says so at the
    /// top of the run, in the signal colour, before the verdict.
    /// </para>
    /// </summary>
    public bool HasSimulatedSteps => Transcript.Any(step => step.IsSimulated);
}

/// <summary>The stock a run created for itself, published so "where did the units come from" has an answer.</summary>
/// <param name="ProductSlug">The fixture product's slug. Unique per run; gone afterwards.</param>
/// <param name="Variants">The variants seeded, with the stock each started with.</param>
/// <param name="Why">Why the lab seeds rather than borrows.</param>
public sealed record LabFixtureDocument(
    string? ProductSlug,
    List<LabFixtureVariantDocument>? Variants,
    string? Why)
{
    /// <summary>The seeded variants, never null.</summary>
    public IReadOnlyList<LabFixtureVariantDocument> All => Variants ?? [];
}

/// <summary>One seeded fixture variant.</summary>
/// <param name="VariantId">The buyable id the run's carts pointed at.</param>
/// <param name="Sku">The SKU a shortfall names.</param>
/// <param name="DisplayName">What it was called.</param>
/// <param name="UnitPrice">The price, chosen so its trailing cents cannot select a payment scenario.</param>
/// <param name="OnHand">Units on the shelf when the run started.</param>
public sealed record LabFixtureVariantDocument(
    Guid VariantId,
    string? Sku,
    string? DisplayName,
    LabMoneyDocument? UnitPrice,
    int OnHand);

/// <summary>
/// Money as the API writes it: minor units, a currency, and the server's own display string.
/// <para>
/// <see cref="Display"/> is used in preference to re-deriving the string here, so a lab transcript
/// and the API's OpenAPI examples cannot disagree about what "$45.00" is. The fallback exists only
/// for a body that arrived without it.
/// </para>
/// </summary>
/// <param name="Amount">Minor units. Never a decimal on the wire, for the reason the API states.</param>
/// <param name="Currency">ISO code.</param>
/// <param name="Display">The server's rendering of the pair.</param>
public sealed record LabMoneyDocument(long Amount, string? Currency, string? Display)
{
    /// <summary>The string to print.</summary>
    public string Text => Display is { Length: > 0 } display
        ? display
        : Catalog.MoneyFormatter.Format(Amount, Currency ?? "USD");
}

/// <summary>
/// One line of the transcript: the HTTP that went out, what came back, and how honest the line is.
/// </summary>
/// <param name="Number">Position in the transcript, from one.</param>
/// <param name="Title">What this step is, in a few words.</param>
/// <param name="Narration">What just happened, in plain English.</param>
/// <param name="Request">The request as sent, or null for a step that is commentary rather than HTTP.</param>
/// <param name="Response">The answer as received, or null for the same reason.</param>
/// <param name="ElapsedMilliseconds">Wall time the caller waited. Null on a note.</param>
/// <param name="Concurrency">How many requests were in flight together at this moment.</param>
/// <param name="Represents">How many identical exchanges this one line stands for.</param>
/// <param name="Fidelity">
/// <c>genuine</c>, <c>elided</c> or <c>not-followed</c> from this server. Anything else is treated
/// as a warning rather than as decoration — see <see cref="IsSimulated"/>.
/// </param>
/// <param name="FidelityNote">The reason, when the fidelity is not <c>genuine</c>.</param>
public sealed record LabStepDocument(
    int Number,
    string? Title,
    string? Narration,
    LabRequestDocument? Request,
    LabResponseDocument? Response,
    long? ElapsedMilliseconds,
    int Concurrency,
    int Represents,
    string? Fidelity,
    string? FidelityNote)
{
    /// <summary>This exact exchange happened, over HTTP, and is printed as it was.</summary>
    public bool IsGenuine => string.Equals(Fidelity, "genuine", StringComparison.OrdinalIgnoreCase);

    /// <summary>It happened, along with others like it that are not printed.</summary>
    public bool IsElided => string.Equals(Fidelity, "elided", StringComparison.OrdinalIgnoreCase);

    /// <summary>It happened, but its asynchronous consequence was not waited for.</summary>
    public bool IsNotFollowed => string.Equals(Fidelity, "not-followed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Anything this build does not recognise as one of the three honest kinds — including a future
    /// <c>simulated</c>. Treated as "this did not necessarily happen" and rendered in the signal
    /// colour with its note forced open, because the alternative is a page that flatters a
    /// transcript it does not understand.
    /// </summary>
    public bool IsSimulated => !IsGenuine && !IsElided && !IsNotFollowed;

    /// <summary>True when this step is a note about the run rather than an exchange with it.</summary>
    public bool IsCommentary => Request is null && Response is null;

    /// <summary>True when nothing answered — the one thing on this page that is a real failure.</summary>
    public bool IsTransportFailure => Response?.Transport is { Length: > 0 };
}

/// <summary>The request as it went out.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Origin-relative path.</param>
/// <param name="Headers">The headers worth showing, with session cookies already redacted by the server.</param>
/// <param name="Body">The body sent, or null.</param>
public sealed record LabRequestDocument(
    string? Method,
    string? Path,
    List<LabHeaderDocument>? Headers,
    string? Body)
{
    /// <summary>The headers, never null.</summary>
    public IReadOnlyList<LabHeaderDocument> All => Headers ?? [];

    /// <summary>"POST /api/checkout" — the line printed on the step itself.</summary>
    public string Line => $"{Method ?? "?"} {Path ?? "?"}";
}

/// <summary>The answer as it came back.</summary>
/// <param name="Status">The status code, or 0 when nothing answered.</param>
/// <param name="Reason">The reason phrase.</param>
/// <param name="Headers">Response headers worth showing, with Set-Cookie redacted.</param>
/// <param name="Body">The body, verbatim, truncated only at the transcript cap.</param>
/// <param name="Transport">Why there was no answer, when there was none.</param>
public sealed record LabResponseDocument(
    int Status,
    string? Reason,
    List<LabHeaderDocument>? Headers,
    string? Body,
    string? Transport)
{
    /// <summary>The headers, never null.</summary>
    public IReadOnlyList<LabHeaderDocument> All => Headers ?? [];

    /// <summary>
    /// Which family the status belongs to, as a CSS modifier.
    /// <para>
    /// 409 and 402 are deliberately <em>not</em> errors here. They are the demonstration: a lab
    /// that painted the forty-five refusals red would be telling a reviewer that the shop had
    /// failed forty-five times, when refusing forty-five times is the entire claim.
    /// </para>
    /// </summary>
    public string Family => Status switch
    {
        0 => "none",
        >= 200 and < 300 => "ok",
        >= 400 and < 500 => "refused",
        _ => "broken",
    };
}

/// <summary>One header, already safe to print.</summary>
/// <param name="Name">Header name.</param>
/// <param name="Value">Header value, redacted by the server where it is a credential.</param>
public sealed record LabHeaderDocument(string? Name, string? Value);

/// <summary>What the database says happened, read after the run and outside every visitor's session.</summary>
/// <param name="Orders">Every order the run created, as the table holds it.</param>
/// <param name="Ledger">The stock ledger for each fixture variant, before and after.</param>
/// <param name="Reservations">Every reservation raised, and the status the reaper reads.</param>
/// <param name="Settlements">The gateway notifications the run produced, and how often each was applied.</param>
/// <param name="BlastRadius">What this run touched that it did not create, and what it left behind.</param>
public sealed record LabEvidenceDocument(
    List<LabOrderDocument>? Orders,
    List<LabLedgerDocument>? Ledger,
    List<LabReservationDocument>? Reservations,
    List<LabSettlementDocument>? Settlements,
    LabBlastRadiusDocument? BlastRadius)
{
    /// <summary>The orders, never null.</summary>
    public IReadOnlyList<LabOrderDocument> AllOrders => Orders ?? [];

    /// <summary>The ledger rows, never null. The before/after pair is the headline of the whole page.</summary>
    public IReadOnlyList<LabLedgerDocument> AllLedger => Ledger ?? [];

    /// <summary>The reservations, never null.</summary>
    public IReadOnlyList<LabReservationDocument> AllReservations => Reservations ?? [];

    /// <summary>The settlements, never null.</summary>
    public IReadOnlyList<LabSettlementDocument> AllSettlements => Settlements ?? [];
}

/// <summary>One order row, as the table holds it.</summary>
/// <param name="OrderNumber">The number the shopper was given.</param>
/// <param name="Status">Where the state machine left it.</param>
/// <param name="Total">What was owed.</param>
/// <param name="Captured">What was actually taken. The number a double-charge would move.</param>
/// <param name="Refunded">What has gone back. The number a double-refund would move.</param>
/// <param name="PlacedAt">When it was created.</param>
/// <param name="PaidAt">When it was paid, if it was.</param>
/// <param name="Quantity">Units across all lines.</param>
/// <param name="Visitor">A short fingerprint of the owning session, never the session id itself.</param>
/// <param name="RowVersion">PostgreSQL's <c>xmin</c>, where a scenario needs it.</param>
public sealed record LabOrderDocument(
    string? OrderNumber,
    string? Status,
    LabMoneyDocument? Total,
    LabMoneyDocument? Captured,
    LabMoneyDocument? Refunded,
    DateTimeOffset PlacedAt,
    DateTimeOffset? PaidAt,
    int Quantity,
    string? Visitor,
    string? RowVersion);

/// <summary>The two numbers the whole stock argument is about, at both ends of the run.</summary>
/// <param name="Sku">The fixture SKU, private to this run.</param>
/// <param name="DisplayName">What the fixture was called.</param>
/// <param name="OnHandBefore">Physical units before.</param>
/// <param name="ReservedBefore">Units promised before.</param>
/// <param name="AvailableBefore">What a shopper could take before.</param>
/// <param name="OnHandAfter">Physical units after.</param>
/// <param name="ReservedAfter">Units promised after.</param>
/// <param name="AvailableAfter">What a shopper could take after.</param>
public sealed record LabLedgerDocument(
    string? Sku,
    string? DisplayName,
    int OnHandBefore,
    int ReservedBefore,
    int AvailableBefore,
    int OnHandAfter,
    int ReservedAfter,
    int AvailableAfter)
{
    /// <summary>Units sold, derived once here so the three places that show it cannot disagree.</summary>
    public int Sold => ReservedAfter - ReservedBefore;
}

/// <summary>One stock reservation.</summary>
/// <param name="Sku">Which fixture variant is held.</param>
/// <param name="OrderNumber">The order holding it, or a placeholder if its order is gone.</param>
/// <param name="Quantity">Units held.</param>
/// <param name="Status">Held, Confirmed or Released.</param>
public sealed record LabReservationDocument(
    string? Sku,
    string? OrderNumber,
    int Quantity,
    string? Status);

/// <summary>One settlement notification, and what the receiver did about it.</summary>
/// <param name="OrderNumber">The order the event refers to.</param>
/// <param name="MessageType">The event type, as the gateway named it.</param>
/// <param name="EventId">The gateway's id for the event — the value the dedupe is keyed on.</param>
/// <param name="Status">Where the outbox row got to.</param>
/// <param name="Attempts">Delivery attempts made by the shop's own dispatcher.</param>
/// <param name="DeliverAfter">The earliest instant it may be sent.</param>
/// <param name="SignatureHeader">The stored <c>X-Vela-Signature</c>, shown in full on purpose.</param>
/// <param name="TimesApplied">The exactly-once claim as an integer. Anything but one is broken.</param>
public sealed record LabSettlementDocument(
    string? OrderNumber,
    string? MessageType,
    string? EventId,
    string? Status,
    int Attempts,
    DateTimeOffset DeliverAfter,
    string? SignatureHeader,
    int TimesApplied);

/// <summary>What a public run endpoint did to everybody else's shop, audited rather than promised.</summary>
/// <param name="StockStrategy">Which strategy this lab uses to get units to race for.</param>
/// <param name="Explanation">Why, and what the alternatives would have cost.</param>
/// <param name="SharedCatalogRowsTouched">Seeded-catalog rows this run wrote. Zero by construction.</param>
/// <param name="FixtureRemoved">Whether nothing is left, re-read after the deletes.</param>
/// <param name="Removed">What was deleted, by table.</param>
/// <param name="Warning">What survived, if anything did. Null on a clean teardown.</param>
public sealed record LabBlastRadiusDocument(
    string? StockStrategy,
    string? Explanation,
    int SharedCatalogRowsTouched,
    bool FixtureRemoved,
    List<LabRowsRemovedDocument>? Removed,
    string? Warning)
{
    /// <summary>The deleted-row tallies, never null.</summary>
    public IReadOnlyList<LabRowsRemovedDocument> All => Removed ?? [];

    /// <summary>True when the teardown left nothing behind and touched nothing it did not create.</summary>
    public bool IsClean => FixtureRemoved && SharedCatalogRowsTouched == 0 && Warning is null;
}

/// <summary>Rows deleted from one table by the teardown.</summary>
/// <param name="Table">The table name, as PostgreSQL holds it.</param>
/// <param name="Rows">How many rows went.</param>
public sealed record LabRowsRemovedDocument(string? Table, int Rows);

/// <summary>Whether the invariant held, and the comparisons that decide it.</summary>
/// <param name="Held">Whether every check passed.</param>
/// <param name="Invariant">The rule, restated so the verdict stands on its own.</param>
/// <param name="HowWeKnow">Which evidence settles it, and why.</param>
/// <param name="Checks">The comparisons, each independently readable.</param>
public sealed record LabVerdictDocument(
    bool Held,
    string? Invariant,
    string? HowWeKnow,
    List<LabCheckDocument>? Checks)
{
    /// <summary>The checks, never null.</summary>
    public IReadOnlyList<LabCheckDocument> All => Checks ?? [];

    /// <summary>How many passed. Shown beside the verdict so "held" is a count rather than a word.</summary>
    public int Passed => All.Count(check => check.Passed);
}

/// <summary>One comparison between what the invariant requires and what the run produced.</summary>
/// <param name="Claim">What this check is about.</param>
/// <param name="Expected">What the invariant requires.</param>
/// <param name="Actual">What the run and the database actually produced.</param>
/// <param name="Passed">Whether they agree.</param>
public sealed record LabCheckDocument(
    string? Claim,
    string? Expected,
    string? Actual,
    bool Passed);

/// <summary>
/// RFC 9457 problem details, as the lab's four refusals send them.
/// <para>
/// Only the three members the page renders. The 503 for a host that mapped the endpoints without
/// calling <c>AddDemoLab</c> puts the missing line in <see cref="Detail"/> verbatim, which is why
/// the page quotes the detail rather than paraphrasing it: it is a working instruction, not an
/// apology.
/// </para>
/// </summary>
/// <param name="Title">The server's own headline.</param>
/// <param name="Detail">The server's own explanation.</param>
/// <param name="Status">The status code, repeated in the body.</param>
public sealed record LabProblemDocument(string? Title, string? Detail, int? Status);

/// <summary>
/// Source-generated readers for everything the lab puts on the wire.
/// <para>
/// Same reason as the catalog's, the cart's and the checkout's contexts: reflection-based
/// serialisation drags the reflection stack into the WebAssembly download and produces trim
/// warnings on a Release publish, which this repository builds with warnings as errors.
/// </para>
/// </summary>
/// <remarks>
/// The camel-case policy is what maps every constructor parameter above onto the wire, because the
/// API serialises with <c>JsonSerializerDefaults.Web</c> and these records are read-only mirrors of
/// its contracts. Case-insensitive matching stays on so a casing change on the wire could never
/// silently null out a verdict.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LabCatalogDocument))]
[JsonSerializable(typeof(LabRunDocument))]
[JsonSerializable(typeof(LabProblemDocument))]
internal sealed partial class LabApiJsonContext : JsonSerializerContext;
