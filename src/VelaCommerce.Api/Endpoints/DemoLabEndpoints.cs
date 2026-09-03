// A PUBLIC, UNAUTHENTICATED ENDPOINT THAT ANSWERS ONE REQUEST WITH A HUNDRED, AND THE FIVE THINGS
// THAT MAKE THAT SAFE TO LEAVE ON THE INTERNET.
//
// The repository claims a set of invariants. The suite proves them on every push, but a reviewer
// with ten minutes will not run a test suite, and a README assertion is worth nothing. So this file
// lets a stranger press a button and watch an invariant hold: the actual HTTP that went out, what
// came back, how long it took, and what changed in the database. That is only worth building if it
// is honest, and only worth deploying if it cannot be turned into a weapon. Hence:
//
// 1. IT RUNS FOR REAL, OVER THE WIRE, AGAINST ITSELF.
//    Every shopper below is a genuine HTTP request from this process to its own listening address:
//    a real session cookie minted by the real middleware, a real cart, a real checkout, a real
//    guarded UPDATE, a real signed webhook delivery. Nothing calls an internal method with a
//    hand-made session id. That distinction is the whole feature - the invariants on display are
//    properties of the COMPOSED system (middleware, limiter, quotas, tenancy filter, endpoint,
//    transaction), and a lab that skipped the pipeline would be demonstrating a smaller claim than
//    the one on the button while looking identical. Where a step is anything less than a live
//    exchange, the step says so in its own Fidelity field rather than in a footnote.
//
// 2. IT NEVER TOUCHES THE SHARED CATALOG'S STOCK.
//    A fifty-way race against a real product would exhaust it for every other visitor - the demo
//    would sell out its own shop, and the first reviewer would ruin it for the rest. So each run
//    SEEDS ITS OWN product, variant and stock ledger, races against that, and destroys all of it
//    before answering. Of the three options (borrow and restore, a permanent lab variant, seed and
//    destroy) this is the only one where a run that dies halfway cannot leave the shelf wrong:
//    restoring depends on the restore step running, and a permanent variant accumulates every
//    order anybody ever raced. The cost is honestly stated in the response: for the second or so
//    that a run lasts, one obviously-named fixture product is visible to the catalog API - not to
//    the storefront, which browses from a static snapshot. That is the entire blast radius, and it
//    is reported as an audited number rather than promised.
//
// 3. NOTHING IT CREATES BELONGS TO ANYBODY.
//    The shoppers are throwaway sessions the run mints and discards; the caller's own cart and
//    orders are never read and never written. Two properties fall out of that. A reviewer cannot
//    use this endpoint to reach another visitor's data, because every row it writes is owned by a
//    session that existed for one second and is then deleted. And the caller cannot be surprised
//    by it either: pressing the button does not add anything to their basket.
//
// 4. THE TEARDOWN IS SCOPED BY THE FIXTURE, AND HAS TO IGNORE THE TENANCY FILTER TO DO IT.
//    The rows to remove belong to those throwaway sessions, so the DemoTenancy filter - which
//    fails closed and correctly shows this request nothing but its own - would delete exactly
//    nothing. The teardown therefore suppresses query filters, and the justification is the scope
//    it uses instead: every statement is keyed on the fixture variant ids MINTED IN THIS REQUEST.
//    They name rows this run created, they cannot match a shopper's order (no shopper can hold a
//    line for a variant that did not exist a second ago and will not exist a second from now), and
//    they are gone from the database before the response is written. This is the one place in the
//    application that deletes rows it does not own, and it is confined to eight statements at the
//    bottom of this file with the fixture id in every WHERE clause.
//
// 5. THE AMPLIFICATION IS BOUNDED THREE WAYS.
//    One accepted request becomes up to 150 more, which is an amplification primitive if left
//    alone. DemoLabThrottle caps runs in flight globally (one), refuses a second run from the same
//    visitor, and holds each visitor to one run per cooldown; DemoLabOptions caps participants and
//    puts a wall-clock budget on the run so a stuck query cannot hold the single slot open. Those
//    sit UNDERNEATH the controls the shop already has - every request this file makes is an
//    ordinary request through the ordinary pipeline, and is rate-limited and quota-checked exactly
//    like a shopper's.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO: sign anything, reserve anything, or decide anything. It
// drives the real endpoints and reads the resulting rows. A lab that reimplemented the signature
// scheme or the reservation statement would be proving that a COPY of the invariant holds, which
// is not evidence about this shop at all.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using VelaCommerce.Api.Contracts;
using VelaCommerce.Api.Tenancy;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Messaging;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.DemoLab;
using VelaCommerce.Infrastructure.Messaging;
using VelaCommerce.Infrastructure.Payments;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Tenancy;

namespace VelaCommerce.Api.Endpoints;

/// <summary>
/// The Demo Lab: a catalogue of the invariants this shop claims, and a button that proves each one
/// against the running system.
/// </summary>
public static class DemoLabEndpoints
{
    /// <summary>
    /// Log category. <c>ILogger&lt;T&gt;</c> is unavailable because a static class cannot be a type
    /// argument, and inventing a marker type to satisfy the generic would be worse than naming the
    /// category once. Matches the convention in <c>CheckoutEndpoints</c> and <c>DemoSafety</c>.
    /// </summary>
    private const string LogCategory = "VelaCommerce.Api.Endpoints.DemoLab";

    /// <summary>The route group. Under <c>/api/demo</c> so it inherits the demo tooling's shape.</summary>
    private const string RouteGroup = "/api/demo/lab";

    /// <summary>
    /// The category every fixture product is filed under.
    /// <para>
    /// Not a plausible-sounding chandlery category, deliberately. A fixture is visible to the
    /// catalog API for the second or so a run lasts, and if anybody ever does see one it must read
    /// immediately as scaffolding rather than as merchandise that has gone missing.
    /// </para>
    /// </summary>
    private const string FixtureCategory = "demo-lab";

    /// <summary>
    /// The fixture unit price, in minor units. <b>$45.00, and the trailing zeros are load-bearing.</b>
    /// The payment simulator reads the last two digits of an amount as a scenario selector - 01 is
    /// Decline, 04 is Delay - so a fixture priced at, say, $45.03 would silently duplicate its
    /// webhooks and make a stock scenario look like a payment bug.
    /// </summary>
    private const long FixturePriceMinorUnits = 4_500;

    /// <summary>A step that happened exactly as printed.</summary>
    private const string Genuine = "genuine";

    /// <summary>A step that happened, along with others like it that are not printed.</summary>
    private const string Elided = "elided";

    /// <summary>A step whose asynchronous consequence this run did not wait to watch.</summary>
    private const string NotFollowed = "not-followed";

    /// <summary>
    /// Camel-case, exactly as the API's own serializer writes and reads. The lab sends the real
    /// request contracts rather than hand-built JSON, so a change to <c>CheckoutRequest</c> breaks
    /// this at compile time instead of at run time.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The address every lab order ships to. A constant because it is not what is being
    /// demonstrated, and because a checkout with a half-filled address is a 400 about validation
    /// rather than a lesson about stock.
    /// </summary>
    private static readonly CheckoutAddressRequest FixtureAddress = new(
        Recipient: "Demo Lab",
        Line1: "1 Chandlery Row",
        Line2: null,
        City: "Portsmouth",
        Region: "Hampshire",
        PostalCode: "PO1 3TX",
        CountryCode: "GB");

    /// <summary>
    /// Maps the Demo Lab.
    /// <para>
    /// Nothing here touches the database, resolves a scoped service or opens a socket: this method
    /// runs during build-time OpenAPI generation, where the entry point is executed against a mock
    /// server as Production, and anything it wrote to stderr would be a build error rather than a
    /// log line.
    /// </para>
    /// <para>
    /// The run endpoint deliberately does not carry <c>RequireRateLimiting</c>. Attaching a named
    /// policy would make this file depend on a policy registered elsewhere, and a host that mapped
    /// the lab without registering it would throw while building the endpoint - during that same
    /// OpenAPI generation. Admission is enforced inside the handler by <see cref="DemoLabThrottle"/>
    /// instead, which cannot fail at startup and produces a better refusal.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapDemoLabEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var lab = app
            .MapGroup(RouteGroup)
            .WithTags("Demo Lab")
            .AddEndpointFilter(PreventSharedCachingAsync);

        lab.MapGet("/scenarios", ListScenarios)
            .WithName("ListDemoLabScenarios")
            .WithSummary("The invariants this shop claims, and what pressing each button will do")
            .WithDescription(
                "Read this before running anything. Every entry states the commercial claim, the "
                + "invariant underneath it, the statement or index that actually enforces it, and "
                + "the test file that proves the same thing in CI - plus how many simultaneous "
                + "shoppers a run creates and which rows it writes, so the cost of a button press "
                + "is known in advance rather than discovered from the transcript. The limits "
                + "block publishes what the endpoint will refuse to do.")
            .Produces<LabScenarioCatalogResponse>();

        lab.MapPost("/run/{scenarioId}", RunScenarioAsync)
            .WithName("RunDemoLabScenario")
            .WithSummary("Run one scenario end to end against the live shop and return the transcript")
            .WithDescription(
                "Seeds its own private product and stock, drives real HTTP checkouts (released "
                + "together on one gate, so races are races rather than a loop), reads the "
                + "resulting rows, destroys everything it created, and returns a transcript: each "
                + "step's raw request and response, the elapsed milliseconds, a plain-English line "
                + "saying what happened, the order and ledger rows afterwards, and an explicit "
                + "verdict naming the invariant and how we know it held. "
                + "The shared catalog is never touched - the stock raced for is created and "
                + "destroyed by the run, and the blast-radius block audits that. "
                + "429 means the lab is busy or this visitor is in cooldown; runs are deliberately "
                + "serialised because two overlapping fifty-way races would exhaust the connection "
                + "pool. Optional ?participants=N lowers the shopper count on the oversell "
                + "scenario for a smaller demonstration.")
            .Produces<LabRunResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// Marks every response here uncacheable by anything shared.
    /// <para>
    /// A transcript is about one run by one visitor, and the first request of a run mints session
    /// cookies. Neither belongs in a shared cache, and the catalogue is cheap enough that caching
    /// it would buy nothing worth the risk of getting the pairing wrong.
    /// </para>
    /// </summary>
    private static async ValueTask<object?> PreventSharedCachingAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers.Append("Vary", "Cookie");

        return await next(context);
    }

    // ---------------------------------------------------------------------------------------
    // The menu.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The catalogue. Static content assembled from two catalogues that already exist, so the page,
    /// the docs and the handler cannot disagree about what a scenario does.
    /// </summary>
    private static Ok<LabScenarioCatalogResponse> ListScenarios(HttpContext http)
    {
        var options = http.RequestServices.GetService<DemoLabOptions>() ?? new DemoLabOptions();

        var scenarios = DemoLabScenarioCatalog.Descriptors
            .Select(descriptor => new LabScenarioResponse(
                descriptor.Id,
                descriptor.Title,
                descriptor.Claim,
                descriptor.Invariant,
                descriptor.Mechanism,
                descriptor.ProvenBy,
                descriptor.ProvenByTest,
                Math.Min(descriptor.Participants, options.MaxParticipants),
                descriptor.Units,
                descriptor.Creates,
                descriptor.Fidelity,
                $"{RouteGroup}/run/{descriptor.Id}"))
            .ToList();

        var payments = PaymentScenarioCatalog.Descriptors
            .Select(descriptor => new LabPaymentScenarioResponse(
                descriptor.Scenario.ToString(),
                descriptor.AmountTrigger,
                descriptor.AuthorizationResult,
                descriptor.Webhooks,
                descriptor.Demonstrates))
            .ToList();

        var limits = new LabLimitsResponse(
            options.Enabled,
            options.MaxParticipants,
            options.MaxConcurrentRuns,
            (int)options.CooldownPerSession.TotalSeconds,
            (int)options.RunTimeout.TotalSeconds,
            "One run at a time across the whole shop, one run per visitor per cooldown, and a "
            + "wall-clock budget on each. A single button press becomes up to 150 real requests, so "
            + "the thing being limited is runs rather than requests - a distinction the shop's "
            + "general rate limiter cannot make, because from outside every run looks like one POST.");

        return TypedResults.Ok(new LabScenarioCatalogResponse(
            scenarios,
            limits,
            payments,
            "Each run seeds its own product, variant and stock, races against that, and destroys it "
            + "before answering. The shared catalog is never sold from, never reserved against and "
            + "never edited: a public button that consumed real inventory would empty the shelf for "
            + "every other visitor, and the first reviewer to press it would spoil the demo for the "
            + "rest."));
    }

    // ---------------------------------------------------------------------------------------
    // The run.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one scenario and returns its transcript.
    /// <para>
    /// The shape is: refuse early and cheaply (unknown scenario, lab not composed, no session, no
    /// admission, no address to call), then seed, then run under a budget, then - whatever happened
    /// - gather evidence and tear the fixture down in a <c>finally</c>. A run that times out or
    /// throws still returns its transcript with an honest verdict, because "it did not finish" is
    /// information and a 500 is not.
    /// </para>
    /// </summary>
    private static async Task<Results<Ok<LabRunResponse>, ProblemHttpResult>> RunScenarioAsync(
        string scenarioId,
        int? participants,
        HttpContext http,
        VelaCommerceDbContext db,
        ICurrentDemoSession session,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!DemoLabScenarioCatalog.TryFind(scenarioId, out var scenario))
        {
            return UnknownScenarioProblem(scenarioId);
        }

        // Resolved optionally rather than as handler parameters. A host that mapped the lab but
        // forgot AddDemoLab would otherwise answer 500 with a DI stack trace; this answers 503 and
        // names the missing call. It also keeps the build-time OpenAPI generator - which composes
        // this entry point for real - from being where a wiring mistake first shows up.
        var services = http.RequestServices;

        if (services.GetService<DemoLabOptions>() is not { } options
            || services.GetService<DemoLabThrottle>() is not { } throttle
            || services.GetService<DemoLabLoopback>() is not { } loopback
            || services.GetService<IDataProtectionProvider>() is not { } dataProtection)
        {
            return NotComposedProblem();
        }

        // The session is never an input, exactly as in DemoEndpoints: it comes from the sealed
        // cookie the middleware bound. It is used only to rate-limit this caller - nothing the run
        // creates is owned by it.
        if (session.SessionId is not { } callerSession)
        {
            return NoDemoSessionProblem();
        }

        var admission = await throttle.EnterAsync(callerSession, cancellationToken);

        if (!admission.Admitted)
        {
            return BusyProblem(http, admission);
        }

        using var lease = admission.Lease!;

        if (ResolveOrigin(http, services) is not { } origin)
        {
            return NoOriginProblem();
        }

        var logger = loggerFactory.CreateLogger(LogCategory);
        var runId = Guid.CreateVersion7().ToString("N")[..12];
        var startedAt = clock.GetUtcNow();
        var startedTicks = Stopwatch.GetTimestamp();

        // The run's own budget, linked to the caller's token so a reviewer who closes the tab stops
        // the work rather than leaving fifty checkouts in flight.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.RunTimeout);

        var run = new LabRun(db, loopback, options, origin, runId, dataProtection, budget.Token);
        LabFixture? fixture = null;
        LabOutcome outcome;

        try
        {
            fixture = await SeedFixtureAsync(db, runId, BlueprintFor(scenario), budget.Token);
            run.Fixture = fixture;
            run.LedgerBefore = await LedgerAsync(db, fixture.VariantIds, budget.Token);

            run.Note(
                "The lab seeds its own stock",
                $"Created a private product ({fixture.Slug}) with "
                + $"{fixture.Variants.Count} variant(s) and "
                + $"{fixture.Variants.Sum(variant => variant.OnHand)} units on the shelf. This is "
                + "not the shop's inventory: the run races against stock it made a moment ago and "
                + "will destroy before answering, so pressing this button cannot sell out anything "
                + "a real visitor was looking at.");

            if (scenario.Id != DemoLabScenarioCatalog.PaymentScenarios)
            {
                run.Caveat(
                    "Every checkout below asks the simulated gateway for the Succeed scenario by "
                    + "name - you can see the hint in each request body. Without it the simulator "
                    + "would read the trailing cents of the order total and refuse or defer about "
                    + "one checkout in twenty, which would make a demonstration about stock look "
                    + "like one about payments. The hint changes which answer the gateway gives; it "
                    + "changes nothing about how stock is reserved, which is what is on trial here.");
            }

            outcome = await ExecuteAsync(run, scenario, participants);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            run.Note(
                "The run ran out of time",
                $"This run exceeded its {options.RunTimeout.TotalSeconds:0.#}-second budget and was "
                + "abandoned. Everything it had already created is still torn down below. A budget "
                + "exists because a run holds the lab's single slot, and an unbounded one would be "
                + "an outage rather than a slow demonstration.");

            outcome = LabOutcome.Inconclusive(
                "The run did not finish, so nothing below is evidence either way. The transcript "
                + "shows how far it got.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Logged with the exception, reported without it. A stack trace in a public response
            // is an information leak; a run that failed and pretended otherwise would be worse.
            logger.LogWarning(
                exception,
                "Demo Lab run {RunId} of scenario {ScenarioId} failed.",
                runId,
                scenario.Id);

            run.Note(
                "The run failed",
                $"The run stopped with {exception.GetType().Name}. The transcript up to that point "
                + "is below, and the fixture is torn down as usual. The failure is in this "
                + "deployment's log under run id " + runId + ".");

            outcome = LabOutcome.Inconclusive(
                "The run did not complete, so the invariant is neither confirmed nor refuted here. "
                + "The test named above is the standing evidence.");
        }
        finally
        {
            // Its own token, deliberately not the run's. A run that timed out or whose caller
            // disconnected must still clean up - a fixture left behind is a product in the catalog
            // and stock nobody can sell.
            using var teardownBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            run.Teardown = fixture is null
                ? LabTeardown.NothingToDo
                : await DestroyFixtureAsync(db, fixture, run.SessionIds, logger, teardownBudget.Token);
        }

        var evidence = run.Evidence ?? await SafeEvidenceAsync(run, logger);

        return TypedResults.Ok(new LabRunResponse(
            runId,
            scenario.Id,
            scenario.Title,
            scenario.Claim,
            scenario.Invariant,
            scenario.Mechanism,
            scenario.ProvenBy,
            scenario.ProvenByTest,
            startedAt,
            (long)Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
            FixtureView(fixture, run.LedgerBefore),
            run.Steps,
            evidence with { BlastRadius = BlastRadiusOf(run.Teardown) },
            new LabVerdictResponse(
                outcome.Checks.Count > 0 && outcome.Checks.All(check => check.Passed),
                scenario.Invariant,
                outcome.HowWeKnow,
                outcome.Checks),
            run.Caveats));
    }

    /// <summary>
    /// Gathers evidence when a scenario failed before it could. Never throws: this runs on the way
    /// out of a failed run, and an exception here would replace a partial transcript with nothing.
    /// </summary>
    private static async Task<LabEvidenceResponse> SafeEvidenceAsync(LabRun run, ILogger logger)
    {
        try
        {
            return await run.EvidenceAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Demo Lab could not read evidence for run {RunId}.", run.RunId);

            return LabRun.EmptyEvidence;
        }
    }

    /// <summary>Dispatches to the scenario named on the route.</summary>
    private static Task<LabOutcome> ExecuteAsync(LabRun run, DemoLabScenarioDescriptor scenario, int? participants) =>
        scenario.Id switch
        {
            DemoLabScenarioCatalog.Oversell => RunOversellAsync(run, scenario, participants),
            DemoLabScenarioCatalog.LastUnit => RunLastUnitAsync(run),
            DemoLabScenarioCatalog.PartialRollback => RunPartialRollbackAsync(run),
            DemoLabScenarioCatalog.DeclinedPayment => RunDeclinedPaymentAsync(run),
            DemoLabScenarioCatalog.DoubleSubmit => RunDoubleSubmitAsync(run),
            DemoLabScenarioCatalog.SettlementReplay => RunSettlementReplayAsync(run),
            DemoLabScenarioCatalog.SettlementRace => RunSettlementRaceAsync(run),
            DemoLabScenarioCatalog.PaymentScenarios => RunPaymentScenariosAsync(run),

            // Unreachable: the id came from TryFind against this same catalogue. Present so that
            // adding a descriptor without adding a runner fails loudly here rather than silently
            // returning an empty verdict that reads like a passing one.
            _ => throw new InvalidOperationException(
                $"Scenario '{scenario.Id}' is in the catalogue but has no runner."),
        };

    /// <summary>How much stock each scenario needs, and what to call it.</summary>
    private static IReadOnlyList<(string Name, int OnHand)> BlueprintFor(DemoLabScenarioDescriptor scenario) =>
        scenario.Id switch
        {
            DemoLabScenarioCatalog.Oversell => [("Storm jib", 5)],
            DemoLabScenarioCatalog.LastUnit => [("Bronze porthole", 1)],
            DemoLabScenarioCatalog.PartialRollback => [("Anchor chain", 10), ("Deck cleat", 10)],
            DemoLabScenarioCatalog.DeclinedPayment => [("Storm lantern", 1)],
            DemoLabScenarioCatalog.DoubleSubmit => [("Brass compass", 3)],
            DemoLabScenarioCatalog.SettlementReplay => [("Anchor lantern", 3)],
            DemoLabScenarioCatalog.SettlementRace => [("Bronze sextant", 2)],
            DemoLabScenarioCatalog.PaymentScenarios => [("Ship's kettle", 6)],
            _ => [("Fixture", 1)],
        };

    // ---------------------------------------------------------------------------------------
    // Scenario: fifty shoppers, five units.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The headline. Fifty genuine checkouts released on one gate against five units.
    /// <para>
    /// Every number asserted below is exact, for the reason the test gives: "at most five" is
    /// satisfied by a shop that sells nothing and "some conflicts" by a shop that refuses
    /// everybody. The status tally is computed from all fifty responses; the transcript prints one
    /// winner and one loser in full and says how many it stood for.
    /// </para>
    /// </summary>
    private static async Task<LabOutcome> RunOversellAsync(
        LabRun run,
        DemoLabScenarioDescriptor scenario,
        int? requested)
    {
        var jib = run.Fixture.Variants[0];
        var shopperCount = run.Clamp(requested ?? scenario.Participants);

        if (shopperCount != scenario.Participants)
        {
            run.Caveat(
                $"This run used {shopperCount} shoppers, not the {scenario.Participants} the claim "
                + "names. The invariant is the same either way, but the demonstration is smaller: "
                + "the number of racers is what makes an oversell likely enough to be worth "
                + "asserting about.");
        }

        var shoppers = await run.NewShoppersAsync(shopperCount);

        run.Record(
            $"{shopperCount} strangers arrive at once",
            $"Each of the {shopperCount} shoppers asks for their cart with no cookie, so the shop "
            + "mints each of them a distinct sealed session. They are genuinely different visitors "
            + "to the server - that is what makes the next step a race rather than one person "
            + "clicking quickly.",
            shoppers.Handshakes[0],
            concurrency: shopperCount,
            represents: shopperCount,
            fidelity: Elided,
            fidelityNote: $"One of {shopperCount} identical handshakes. All of them ran; "
                          + "printing fifty copies of the same 200 would bury the interesting part.");

        var adds = await LabRun.AllAtOnceAsync(
            shopperCount,
            index => run.SendAsync(run.AddToCart(shoppers.All[index], jib.VariantId, 1)));

        run.Record(
            "Each puts one in their cart",
            $"All {shopperCount} carts now hold one {jib.Sku}. Adding to a cart takes no stock - "
            + $"nothing is reserved until checkout, which is why {shopperCount} carts can each hold "
            + $"a unit of an item with only {jib.OnHand} on the shelf.",
            adds[0],
            concurrency: shopperCount,
            represents: shopperCount,
            fidelity: Elided,
            fidelityNote: $"One of {shopperCount} identical adds.");

        var checkouts = await LabRun.AllAtOnceAsync(
            shopperCount,
            index => run.SendAsync(run.Checkout(shoppers.All[index], $"lab-{run.RunId}-{index:00}")));

        var created = checkouts.Where(exchange => exchange.StatusCode == StatusCodes.Status201Created).ToList();
        var refused = checkouts.Where(exchange => exchange.StatusCode == StatusCodes.Status409Conflict).ToList();
        var other = checkouts.Where(exchange =>
            exchange.StatusCode is not (StatusCodes.Status201Created or StatusCodes.Status409Conflict)).ToList();

        run.Note(
            $"All {shopperCount} press Pay on the same tick",
            $"The {shopperCount} checkouts were built first and released together on one gate, so "
            + "they are inside the critical section at the same moment. Started in a loop instead, "
            + "the first would usually have committed before the second was written and the whole "
            + "exercise would prove only that a shop can handle one thing at a time. "
            + $"The shop answered: {created.Count} created, {refused.Count} refused, {other.Count} "
            + "anything else.");

        if (created.Count > 0)
        {
            run.Record(
                "A winner",
                "201 Created, and the body is a paid order: the guarded UPDATE returned a row count "
                + "of 1 for this shopper, so the units were theirs before the payment was even "
                + "attempted.",
                created[0],
                concurrency: shopperCount,
                represents: created.Count,
                fidelity: created.Count > 1 ? Elided : Genuine,
                fidelityNote: created.Count > 1
                    ? $"One of {created.Count} identical outcomes; the others differ only in order number."
                    : null);
        }

        if (refused.Count > 0)
        {
            run.Record(
                "A loser",
                "409 Conflict, and the body names the item rather than apologising in general terms: "
                + "the same UPDATE returned a row count of 0 because on_hand - reserved had already "
                + "fallen below what this shopper asked for. Losing a race for stock is an ordinary "
                + "commercial outcome, so it arrives as a 409 with a shortfall a storefront can "
                + "render - not as a 500 and not as a constraint violation leaking out of the "
                + "database.",
                refused[0],
                concurrency: shopperCount,
                represents: refused.Count,
                fidelity: refused.Count > 1 ? Elided : Genuine,
                fidelityNote: refused.Count > 1 ? $"One of {refused.Count} identical refusals." : null);
        }

        if (other.Count > 0)
        {
            run.Record(
                "An answer that should not exist",
                "Neither 201 nor 409. That means the race was resolved by something other than the "
                + "guarded UPDATE - a constraint violation, a deadlock, a timeout - and this run has "
                + "found a genuine defect. The body is the only thing that says which.",
                other[0],
                concurrency: shopperCount,
                represents: other.Count);
        }

        var evidence = await run.EvidenceAsync();
        var ledger = evidence.Ledger[0];
        // What CAN sell, not what is on the shelf. With fewer shoppers than units every shopper
        // wins and nobody is refused — a correct outcome that the old expectation called a
        // failure, printing "expected: -3" refusals above a red verdict on a healthy shop.
        var units = Math.Min(shopperCount, jib.OnHand);

        return new LabOutcome(
            "Not from the status codes - a shop that answered politely and reserved eight units "
            + "would produce an identical transcript. From the ledger: reserved finished at "
            + $"{ledger.ReservedAfter} against on_hand of {ledger.OnHandAfter}, which means "
            + $"{ledger.ReservedAfter} units are promised and none are promised twice. The "
            + "database's own ck_stock_items_reserved_within_on_hand check would have refused a "
            + "sixth, which is why an oversell would have surfaced above as a 500 rather than as a "
            + "wrong number here.",
            [
                Check(
                    "Orders created",
                    units.ToString(CultureInfo.InvariantCulture),
                    created.Count.ToString(CultureInfo.InvariantCulture),
                    created.Count == units),
                Check(
                    "Shoppers told the item ran out",
                    (shopperCount - units).ToString(CultureInfo.InvariantCulture),
                    refused.Count.ToString(CultureInfo.InvariantCulture),
                    refused.Count == shopperCount - units),
                Check(
                    "Answers that were neither 201 nor 409",
                    "0",
                    other.Count.ToString(CultureInfo.InvariantCulture),
                    other.Count == 0),
                Check(
                    "Units reserved in the ledger afterwards",
                    $"{units} of {jib.OnHand} on hand",
                    $"{ledger.ReservedAfter} of {ledger.OnHandAfter} on hand",
                    ledger.ReservedAfter == units && ledger.OnHandAfter == jib.OnHand),
                Check(
                    "Distinct order numbers (not one order counted five times)",
                    units.ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal).Count()
                        .ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal).Count() == units),
                Check(
                    "Distinct visitors holding those orders",
                    units.ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Select(order => order.Visitor).Distinct(StringComparer.Ordinal).Count()
                        .ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Select(order => order.Visitor).Distinct(StringComparer.Ordinal).Count() == units),
                Check(
                    "Every order paid in full",
                    "captured equals total on all of them",
                    Describe(evidence.Orders, order => order.Captured.Amount == order.Total.Amount),

                    // The emptiness guard is not pedantry. "All of no orders were paid in full" is
                    // true, and a run that sold nothing at all would otherwise show this line
                    // passing among a column of failures - which is exactly the kind of
                    // half-reassuring verdict this lab exists to avoid producing.
                    evidence.Orders.Count > 0
                    && evidence.Orders.All(order => order.Captured.Amount == order.Total.Amount)),
                Check(
                    "Reservations left Confirmed, so the reaper will not hand the units back",
                    $"{units} Confirmed",
                    $"{evidence.Reservations.Count(row => row.Status == nameof(ReservationStatus.Confirmed))} Confirmed "
                    + $"of {evidence.Reservations.Count}",
                    evidence.Reservations.Count == units
                    && evidence.Reservations.All(row => row.Status == nameof(ReservationStatus.Confirmed))),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: two shoppers, one unit.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The same race at the smallest size that still is one, printed in full so the refusal body
    /// can be read field by field. "Something went wrong, try again" is the failure mode the 409
    /// is designed against.
    /// </summary>
    private static async Task<LabOutcome> RunLastUnitAsync(LabRun run)
    {
        var porthole = run.Fixture.Variants[0];
        var shoppers = await run.NewShoppersAsync(2);

        run.Record(
            "Two strangers arrive",
            "Two separate sealed sessions, minted by the shop. Nothing distinguishes them from two "
            + "people on two laptops.",
            shoppers.Handshakes[0],
            concurrency: 2,
            represents: 2,
            fidelity: Elided,
            fidelityNote: "One of two identical handshakes.");

        var adds = await LabRun.AllAtOnceAsync(
            2,
            index => run.SendAsync(run.AddToCart(shoppers.All[index], porthole.VariantId, 1)));

        run.Record(
            "Both put the last one in their cart",
            $"There is exactly one {porthole.Sku} on the shelf and both carts now claim it. Neither "
            + "shopper has taken anything yet: a cart line is an intention, and stock moves at "
            + "checkout.",
            adds[0],
            concurrency: 2,
            represents: 2,
            fidelity: Elided,
            fidelityNote: "One of two identical adds.");

        var checkouts = await LabRun.AllAtOnceAsync(
            2,
            index => run.SendAsync(run.Checkout(shoppers.All[index], $"lab-{run.RunId}-{index}")));

        var sold = checkouts.FirstOrDefault(exchange => exchange.StatusCode == StatusCodes.Status201Created);
        var lost = checkouts.FirstOrDefault(exchange => exchange.StatusCode == StatusCodes.Status409Conflict);

        foreach (var (exchange, index) in checkouts.Select((exchange, index) => (exchange, index)))
        {
            run.Record(
                exchange.StatusCode switch
                {
                    StatusCodes.Status201Created => "The one who got it",
                    StatusCodes.Status409Conflict => "The one who did not",
                    _ => "An answer that should not exist",
                },
                exchange.StatusCode switch
                {
                    StatusCodes.Status201Created =>
                        "201, with a paid order. One conditional UPDATE decided this, and it decided "
                        + "it inside the database against a locked row - not in C# that happened to "
                        + "run first.",
                    StatusCodes.Status409Conflict =>
                        "409, and read the shortfall: it names the variant, the SKU, how many were "
                        + "asked for and how many were available. That is what lets a storefront "
                        + "highlight the row instead of showing a general apology, and it is why the "
                        + "detail line mentions the SKU by name.",
                    _ =>
                        "Neither 201 nor 409, which means something other than the guarded UPDATE "
                        + "resolved this race.",
                },
                exchange,
                concurrency: 2);
        }

        var evidence = await run.EvidenceAsync();
        var ledger = evidence.Ledger[0];
        var shortfall = ShortfallOf(lost);

        return new LabOutcome(
            "Two requests, one unit, and exactly one order row afterwards. The refusal is checked "
            + "field by field rather than by status code alone, because a 409 that said nothing "
            + "useful would satisfy the letter of the invariant and none of its point.",
            [
                Check(
                    "One sale and one refusal",
                    "201 and 409",
                    string.Join(" and ", checkouts.Select(exchange => exchange.StatusCode)),
                    sold is not null && lost is not null),
                Check(
                    "The refusal names the item that ran out",
                    porthole.Sku,
                    shortfall?.Sku ?? "(no shortfall in the body)",
                    string.Equals(shortfall?.Sku, porthole.Sku, StringComparison.Ordinal)),
                Check(
                    "The refusal says how many were left",
                    "requested 1, available 0",
                    shortfall is null
                        ? "(no shortfall in the body)"
                        : $"requested {shortfall.Requested}, available {shortfall.Available?.ToString(CultureInfo.InvariantCulture) ?? "none recorded"}",
                    shortfall is { Requested: 1, Available: 0 }),
                Check(
                    "Orders created",
                    "1",
                    evidence.Orders.Count.ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Count == 1),
                Check(
                    "The ledger afterwards",
                    "1 reserved of 1 on hand",
                    $"{ledger.ReservedAfter} reserved of {ledger.OnHandAfter} on hand",
                    ledger is { ReservedAfter: 1, OnHandAfter: 1 }),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: a cart that cannot be filled buys nothing.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The failure that quietly costs a real shop money: stock taken for a line, then the checkout
    /// abandons, and the units are promised to an order that will never exist.
    /// <para>
    /// The roles are assigned by variant id using the same comparison the reservation loop uses,
    /// because that loop orders by variant id to make two shoppers buying the same two items in
    /// opposite cart order unable to deadlock. Assigned the other way round, this scenario would
    /// exercise the rollback about half the time and look flaky rather than wrong.
    /// </para>
    /// </summary>
    private static async Task<LabOutcome> RunPartialRollbackAsync(LabRun run)
    {
        var (first, second) = run.Fixture.Variants[0].VariantId.CompareTo(run.Fixture.Variants[1].VariantId) < 0
            ? (run.Fixture.Variants[0], run.Fixture.Variants[1])
            : (run.Fixture.Variants[1], run.Fixture.Variants[0]);

        var plentiful = first;
        var exhausted = second;

        run.Note(
            "Two items, and which is reserved first is not left to chance",
            $"{plentiful.Sku} sorts before {exhausted.Sku} by variant id, and checkout reserves "
            + "lines in variant-id order so two shoppers buying the same two items in opposite cart "
            + "order cannot deadlock. So a cart holding both will take the plentiful one FIRST and "
            + "then discover the shortage - which is the only ordering in which there is anything "
            + "to give back.");

        var holder = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(holder, plentiful.VariantId, 3));

        var held = await run.SendAsync(run.Checkout(holder, $"lab-{run.RunId}-hold"));

        run.Record(
            "Another shopper is already holding three",
            $"A real checkout, so three of the ten {plentiful.Sku} are genuinely reserved by "
            + "somebody else. This matters: it makes \"back where it started\" a number this run "
            + "could get wrong, rather than a zero it could stumble into.",
            held);

        var drainer = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(drainer, exhausted.VariantId, 10));

        var drained = await run.SendAsync(run.Checkout(drainer, $"lab-{run.RunId}-drain"));

        run.Record(
            "A third shopper takes every one of the other item",
            $"All ten {exhausted.Sku} are now reserved - again by a genuine checkout rather than by "
            + "writing a number into the ledger. The shortage the next shopper hits is real.",
            drained);

        var victim = await run.NewShopperAsync();
        var addedPlentiful = await run.SendAsync(run.AddToCart(victim, plentiful.VariantId, 2));
        await run.SendAsync(run.AddToCart(victim, exhausted.VariantId, 1));

        run.Record(
            "A shopper builds a two-line cart",
            $"Two {plentiful.Sku} (seven still free) and one {exhausted.Sku} (none free). Only the "
            + "first line can be filled.",
            addedPlentiful,
            represents: 2,
            fidelity: Elided,
            fidelityNote: "The second add, for the exhausted item, answered identically.");

        var refused = await run.SendAsync(run.Checkout(victim, $"lab-{run.RunId}-partial"));

        run.Record(
            "The checkout refuses the whole cart",
            "409, naming the line that could not be filled. The interesting part is what happened "
            + "to the two units the checkout had ALREADY taken for the first line: the reservations "
            + "are uncommitted increments inside one transaction, so rolling back IS the release. "
            + "Not a compensating step somebody could forget to write - there is nothing to forget.",
            refused);

        var midLedger = await LedgerAsync(run.Database, [plentiful.VariantId], run.Token);
        var afterAttempt = midLedger[plentiful.VariantId];

        run.Note(
            "The ledger is exactly where it was",
            $"{plentiful.Sku}: {afterAttempt.Reserved} reserved of {afterAttempt.OnHand}. The three "
            + "somebody else holds are untouched and the two this checkout took are gone again. A "
            + "shop that released line by line as a follow-up step would show five here, and "
            + "nothing would ever report it - the units would simply be unsellable until somebody "
            + "noticed the ledger drifting.");

        var next = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(next, plentiful.VariantId, 7));

        var sale = await run.SendAsync(run.Checkout(next, $"lab-{run.RunId}-after"));

        run.Record(
            "And the units are genuinely free, not merely reported as free",
            "The next shopper takes all seven that remain. They could not do that if two were still "
            + "stranded in a reservation belonging to an order that was never created - the guarded "
            + "UPDATE would have refused this checkout too.",
            sale);

        var evidence = await run.EvidenceAsync();
        var plentifulLedger = evidence.Ledger.Single(row => row.Sku == plentiful.Sku);
        var shortfall = ShortfallOf(refused);

        return new LabOutcome(
            "From the ledger between the failed checkout and the next one. The refusal alone proves "
            + "nothing - a shop that refused the cart AND kept the two units would answer 409 too. "
            + "What settles it is that the reserved count returned to the three another shopper was "
            + "holding, and then that a later shopper could actually buy all seven of the rest.",
            [
                Check(
                    "The checkout was refused",
                    "409",
                    refused.StatusCode.ToString(CultureInfo.InvariantCulture),
                    refused.StatusCode == StatusCodes.Status409Conflict),
                Check(
                    "The refusal names the line that could not be filled",
                    exhausted.Sku,
                    shortfall?.Sku ?? "(no shortfall in the body)",
                    string.Equals(shortfall?.Sku, exhausted.Sku, StringComparison.Ordinal)),
                Check(
                    "Units the failed checkout gave back",
                    "2 of 2 - reserved returns to 3",
                    $"reserved was {afterAttempt.Reserved} immediately after the refusal",
                    afterAttempt.Reserved == 3),
                Check(
                    "The next shopper could take all seven remaining",
                    "201",
                    sale.StatusCode.ToString(CultureInfo.InvariantCulture),
                    sale.StatusCode == StatusCodes.Status201Created),
                Check(
                    "The ledger at the end",
                    "10 reserved of 10 on hand",
                    $"{plentifulLedger.ReservedAfter} reserved of {plentifulLedger.OnHandAfter} on hand",
                    plentifulLedger is { ReservedAfter: 10, OnHandAfter: 10 }),
                Check(
                    "The refused cart left no order behind",
                    "3 orders, none of them the refused one",
                    $"{evidence.Orders.Count} orders",
                    evidence.Orders.Count == 3),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: a declined card gives the unit back.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A refused card, and the half of the outcome that is easy to get wrong: what is NOT undone.
    /// </summary>
    private static async Task<LabOutcome> RunDeclinedPaymentAsync(LabRun run)
    {
        var lantern = run.Fixture.Variants[0];

        var unlucky = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(unlucky, lantern.VariantId, 1));

        var declined = await run.SendAsync(
            run.Checkout(unlucky, $"lab-{run.RunId}-declined", scenario: nameof(PaymentSimulatorScenario.Decline)));

        run.Record(
            "A shopper's card is refused",
            "402, with a payment block saying Declined and why. The scenario hint asks the simulated "
            + "gateway for a refusal, which is the same path a real one would take: the gateway call "
            + "sits BETWEEN two transactions, never inside one, so no row is locked while an external "
            + "service thinks about it. A refused card is a business answer, not an exception.",
            declined);

        var afterDecline = await LedgerAsync(run.Database, [lantern.VariantId], run.Token);
        var released = afterDecline[lantern.VariantId];

        run.Note(
            "The unit is back on the shelf, and the paperwork is not",
            $"{lantern.Sku}: {released.Reserved} reserved of {released.OnHand}. The order survives "
            + "as Cancelled rather than being rolled away - the attempt really happened, and that "
            + "row is what keeps the idempotency key spent. Without it, a frantically re-clicked "
            + "Pay would mint a new order number, and therefore a new gateway reference, and "
            + "therefore a second chance at a second real charge.");

        var cart = await run.SendAsync(run.Cart(unlucky));

        run.Record(
            "The cart survived",
            "The shopper can fix their card rather than rebuild their basket. Only the stock was "
            + "returned - by a guarded UPDATE that mirrors the one that took it.",
            cart);

        var next = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(next, lantern.VariantId, 1));

        var sold = await run.SendAsync(run.Checkout(next, $"lab-{run.RunId}-after-decline"));

        run.Record(
            "And somebody else buys it",
            "201. This is the whole point of releasing the unit: a declined card must not take an "
            + "item off the market for the fifteen minutes a reservation would otherwise hold it.",
            sold);

        var evidence = await run.EvidenceAsync();
        var cancelled = evidence.Orders.FirstOrDefault(order => order.Status == nameof(OrderStatus.Cancelled));
        var paid = evidence.Orders.FirstOrDefault(order => order.Status == nameof(OrderStatus.Paid));
        var ledger = evidence.Ledger[0];

        return new LabOutcome(
            "From three rows read after the fact: the ledger going back to zero reserved between the "
            + "402 and the next checkout, the cancelled order still sitting there with nothing "
            + "captured, and its reservation marked Released rather than deleted. The last shopper's "
            + "201 is the practical proof - a unit that was still held could not have been sold.",
            [
                Check(
                    "The refused checkout answered 402, not 500",
                    "402",
                    declined.StatusCode.ToString(CultureInfo.InvariantCulture),
                    declined.StatusCode == StatusCodes.Status402PaymentRequired),
                Check(
                    "Stock released immediately after the decline",
                    "0 reserved of 1 on hand",
                    $"{released.Reserved} reserved of {released.OnHand} on hand",
                    released is { Reserved: 0, OnHand: 1 }),
                Check(
                    "The order survives as Cancelled with nothing captured",
                    "Cancelled, captured 0",
                    cancelled is null
                        ? "(no cancelled order)"
                        : $"{cancelled.Status}, captured {cancelled.Captured.Display}",
                    cancelled is not null && cancelled.Captured.Amount == 0),
                Check(
                    "The reservation is Released, not deleted",
                    "1 Released",
                    Describe(evidence.Reservations, row => row.Status == nameof(ReservationStatus.Released)),
                    evidence.Reservations.Any(row => row.Status == nameof(ReservationStatus.Released))),
                Check(
                    "The shopper's cart still holds the item",
                    "1 unit",
                    CartQuantityOf(cart)?.ToString(CultureInfo.InvariantCulture) ?? "(unreadable)",
                    CartQuantityOf(cart) == 1),
                Check(
                    "The next shopper could buy the released unit",
                    "201, Paid",
                    $"{sold.StatusCode}, {paid?.Status ?? "(no paid order)"}",
                    sold.StatusCode == StatusCodes.Status201Created && paid is not null),
                Check(
                    "The ledger at the end",
                    "1 reserved of 1 on hand",
                    $"{ledger.ReservedAfter} reserved of {ledger.OnHandAfter} on hand",
                    ledger is { ReservedAfter: 1, OnHandAfter: 1 }),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: a double-clicked Pay button.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Two identical submissions on one gate, and then the same key from a different visitor - the
    /// second half being what shows the key is scoped to a session rather than global.
    /// </summary>
    private static async Task<LabOutcome> RunDoubleSubmitAsync(LabRun run)
    {
        var compass = run.Fixture.Variants[0];
        var key = $"lab-{run.RunId}-double-click";

        var shopper = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(shopper, compass.VariantId, 1));

        var submissions = await LabRun.AllAtOnceAsync(
            2,
            _ => run.SendAsync(run.Checkout(shopper, key)));

        var winner = submissions.FirstOrDefault(exchange => exchange.StatusCode == StatusCodes.Status201Created);
        var replay = submissions.FirstOrDefault(exchange => exchange.StatusCode == StatusCodes.Status200OK);

        run.Note(
            "One shopper, one cart, two simultaneous submissions of the same idempotency key",
            "Both requests carry the same Idempotency-Key header and are released together. Both are "
            + "allowed to insert: the fix is not to SELECT for an existing key first, because two "
            + "simultaneous submits both find nothing and both insert - that is the race, not the "
            + "cure. ux_orders_demo_session_id_idempotency_key picks the winner, and the loser "
            + "catches the unique violation, rolls back - releasing its own stock reservations with "
            + "it - and hands back the winner's order.");

        foreach (var submission in submissions)
        {
            run.Record(
                submission.StatusCode switch
                {
                    StatusCodes.Status201Created => "The click that made the order",
                    StatusCodes.Status200OK => "The click that did not",
                    _ => "An answer that should not exist",
                },
                submission.StatusCode switch
                {
                    StatusCodes.Status201Created =>
                        "201: this insert won the unique index.",
                    StatusCodes.Status200OK =>
                        "200, not an error - and the body is the SAME order, with the same number "
                        + "and the same captured amount. A shopper who double-clicked sees one "
                        + "confirmation, which is the only outcome that is not either a lie or a "
                        + "second charge.",
                    _ =>
                        "Neither 201 nor 200, which means the double submit was resolved by "
                        + "something other than the unique index.",
                },
                submission,
                concurrency: 2);
        }

        var stranger = await run.NewShopperAsync();
        await run.SendAsync(run.AddToCart(stranger, compass.VariantId, 1));

        var reused = await run.SendAsync(run.Checkout(stranger, key));

        run.Record(
            "A different visitor sends the very same key",
            "201, and their own order. The uniqueness is on (demo_session_id, idempotency_key), not "
            + "on the key alone - so one visitor's client-generated key cannot collide with "
            + "another's and quietly hand them somebody else's order. Two orders now exist for one "
            + "key, which is correct.",
            reused);

        var evidence = await run.EvidenceAsync();
        var ledger = evidence.Ledger[0];
        var winnerNumber = OrderNumberOf(winner);
        var replayNumber = OrderNumberOf(replay);

        return new LabOutcome(
            "From the order table rather than from the two status codes: one row for the "
            + "double-clicking visitor and one for the stranger, and a ledger showing two units "
            + "reserved rather than four. Both halves matter - the first shows the duplicate was "
            + "absorbed, the second shows the loser's rollback took its stock reservation with it.",
            [
                Check(
                    "The double submit answered once Created and once OK",
                    "201 and 200",
                    string.Join(" and ", submissions.Select(exchange => exchange.StatusCode)),
                    winner is not null && replay is not null),
                Check(
                    "Both answers describe the same order",
                    "identical order numbers",
                    winnerNumber is null || replayNumber is null
                        ? "(an order number was unreadable)"
                        : $"{winnerNumber} and {replayNumber}",
                    winnerNumber is not null && string.Equals(winnerNumber, replayNumber, StringComparison.Ordinal)),
                Check(
                    "Orders created in total",
                    "2 - one per visitor",
                    evidence.Orders.Count.ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Count == 2),
                Check(
                    "Those orders belong to two different visitors",
                    "2 distinct visitors",
                    evidence.Orders.Select(order => order.Visitor).Distinct(StringComparer.Ordinal).Count()
                        .ToString(CultureInfo.InvariantCulture),
                    evidence.Orders.Select(order => order.Visitor).Distinct(StringComparer.Ordinal).Count() == 2),
                Check(
                    "Units reserved (a duplicated order would show four)",
                    "2 of 3 on hand",
                    $"{ledger.ReservedAfter} of {ledger.OnHandAfter} on hand",
                    ledger is { ReservedAfter: 2, OnHandAfter: 3 }),
                Check(
                    "The stranger's identical key created their own order",
                    "201",
                    reused.StatusCode.ToString(CultureInfo.InvariantCulture),
                    reused.StatusCode == StatusCodes.Status201Created),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: a settlement delivered twice.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The gateway's own bytes, redelivered. Read out of the outbox row checkout wrote and posted
    /// verbatim with their stored signature - a receiver that deduplicated on content rather than
    /// on event id would pass a test built any other way and fail in production on the first retry.
    /// </summary>
    private static async Task<LabOutcome> RunSettlementReplayAsync(LabRun run)
    {
        var (order, notification, setup) = await run.PlaceDeferredOrderAsync("replay");

        if (order is null || notification is null)
        {
            return LabOutcome.Inconclusive(setup);
        }

        var beforeFirst = await run.RowVersionAsync(order);

        var first = await run.SendAsync(run.Deliver(notification));

        run.Record(
            "The gateway delivers the settlement",
            "200, and the acknowledgement says settled: the event id was inserted into "
            + "processed_webhook_events and the order transitioned to Paid IN ONE TRANSACTION. The "
            + "signature was verified over the exact bytes that arrived, never over a "
            + "re-serialization - a proxy that reformatted this JSON in flight would break it, which "
            + "is the correct behaviour for a MAC.",
            first);

        var afterFirst = await run.RowVersionAsync(order);
        var paidSnapshot = await run.OrderSnapshotAsync(order);

        var second = await run.SendAsync(run.Deliver(notification));

        run.Record(
            "The gateway delivers it again, byte for byte",
            "200 again, and the acknowledgement says duplicate. Not a 409: a non-2xx would be "
            + "retried five times and abandoned with an alarming last error, on every duplicate, "
            + "for a delivery that was handled perfectly. The duplicate is also told what the order "
            + "already is, which is what turns \"duplicate\" into something a sender can act on "
            + "rather than merely stop retrying about.",
            second);

        var afterSecond = await run.RowVersionAsync(order);
        var finalSnapshot = await run.OrderSnapshotAsync(order);

        run.Note(
            "PostgreSQL's own account of whether the row was written again",
            $"xmin is the id of the transaction that last wrote the tuple, so it changes on every "
            + $"UPDATE and on nothing else. Before the first delivery: {beforeFirst ?? "unknown"}. "
            + $"After it: {afterFirst ?? "unknown"}. After the duplicate: {afterSecond ?? "unknown"}. "
            + "That is the one assertion a duplicate cannot satisfy by writing the same values a "
            + "second time - which is the difference between \"nothing changed\" and \"exactly-once\".");

        run.Caveat(
            "The shop's own outbox dispatcher is running throughout, and it is entitled to deliver "
            + "this same notification on its next sweep. That is not a flaw in the demonstration - "
            + "it is precisely the at-least-once condition the invariant exists for, and it is why "
            + "the verdict below is decided by counting rows in processed_webhook_events rather than "
            + "by assuming which sender got there first.");

        var evidence = await run.EvidenceAsync();
        var settlement = evidence.Settlements.FirstOrDefault();

        return new LabOutcome(
            "From three readings the status codes cannot fake: exactly one row in "
            + "processed_webhook_events for this event id, a captured amount equal to the total "
            + "rather than twice it, and an unchanged xmin across the duplicate. A receiver that "
            + "answered 200 twice and paid twice would produce identical status codes; so would one "
            + "that answered 200 twice and paid nothing.",
            [
                Check(
                    "The first delivery was applied",
                    "200, outcome settled",
                    $"{first.StatusCode}, outcome {AcknowledgementField(first, "outcome") ?? "unreadable"}",
                    first.StatusCode == StatusCodes.Status200OK),
                Check(
                    "The duplicate was accepted and not applied",
                    "200, outcome duplicate",
                    $"{second.StatusCode}, outcome {AcknowledgementField(second, "outcome") ?? "unreadable"}",
                    second.StatusCode == StatusCodes.Status200OK
                    && string.Equals(AcknowledgementField(second, "outcome"), "duplicate", StringComparison.Ordinal)),
                Check(
                    "Times this event id appears in processed_webhook_events",
                    "1",
                    settlement?.TimesApplied.ToString(CultureInfo.InvariantCulture) ?? "(no settlement row)",
                    settlement?.TimesApplied == 1),
                Check(
                    "The order was paid once",
                    "captured equals total",
                    finalSnapshot is null
                        ? "(order unreadable)"
                        : $"captured {finalSnapshot.Captured.Display} of {finalSnapshot.Total.Display}",
                    finalSnapshot is not null && finalSnapshot.Captured.Amount == finalSnapshot.Total.Amount),
                Check(
                    "The order row was not written again by the duplicate",
                    "xmin unchanged",
                    $"{afterFirst ?? "unknown"} then {afterSecond ?? "unknown"}",
                    afterFirst is not null && string.Equals(afterFirst, afterSecond, StringComparison.Ordinal)),
                Check(
                    "Nothing else about the order moved either",
                    "same status and captured amount",
                    finalSnapshot is null || paidSnapshot is null
                        ? "(order unreadable)"
                        : $"{paidSnapshot.Status}/{paidSnapshot.Captured.Amount} then {finalSnapshot.Status}/{finalSnapshot.Captured.Amount}",
                    paidSnapshot is not null
                    && finalSnapshot is not null
                    && paidSnapshot.Status == finalSnapshot.Status
                    && paidSnapshot.Captured.Amount == finalSnapshot.Captured.Amount),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: two copies of one settlement, at once.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The test the primary key exists for. The sequential case passes against a receiver that
    /// merely asks "have I seen this?" first; two deliveries genuinely in flight together both
    /// find nothing, both decide to apply, and both proceed.
    /// </summary>
    private static async Task<LabOutcome> RunSettlementRaceAsync(LabRun run)
    {
        var (order, notification, setup) = await run.PlaceDeferredOrderAsync("race");

        if (order is null || notification is null)
        {
            return LabOutcome.Inconclusive(setup);
        }

        var deliveries = await LabRun.AllAtOnceAsync(2, _ => run.SendAsync(run.Deliver(notification)));

        run.Note(
            "Two copies of one event, released together",
            "Both deliveries carry the same event id, the same bytes and the same signature, and "
            + "they are inside the receiver at the same moment. There is deliberately no \"have I "
            + "seen this event?\" query in front of the insert: both would find nothing and both "
            + "would proceed. The insert races pk_processed_webhook_events inside the same "
            + "transaction as the transition, so the loser's rollback takes its transition with it.");

        foreach (var delivery in deliveries)
        {
            var outcome = AcknowledgementField(delivery, "outcome");

            run.Record(
                outcome switch
                {
                    "settled" => "The copy that moved the order",
                    "duplicate" => "The copy that lost the race",
                    _ => "An answer that should not exist",
                },
                outcome switch
                {
                    "settled" => "200, applied: this transaction inserted the event id and paid the "
                                 + "order together.",
                    "duplicate" => "200, not applied. A lost race here surfacing as a 500 would be "
                                   + "retried five times and then abandoned - a payment silently not "
                                   + "applied because two copies of it arrived too close together.",
                    _ => "Neither settled nor duplicate. Something other than the primary key "
                         + "resolved this.",
                },
                delivery,
                concurrency: 2);
        }

        var applied = deliveries.Count(delivery =>
            string.Equals(AcknowledgementField(delivery, "outcome"), "settled", StringComparison.Ordinal));

        var duplicates = deliveries.Count(delivery =>
            string.Equals(AcknowledgementField(delivery, "outcome"), "duplicate", StringComparison.Ordinal));

        run.Caveat(
            "The shop's own outbox dispatcher may also deliver this notification while the run is in "
            + "flight. If it wins, both copies above answer duplicate and the count of applied "
            + "deliveries here is zero while processed_webhook_events still holds exactly one row - "
            + "which is the invariant holding, not failing. The verdict counts rows for that reason.");

        var evidence = await run.EvidenceAsync();
        var settlement = evidence.Settlements.FirstOrDefault();
        var snapshot = await run.OrderSnapshotAsync(order);

        return new LabOutcome(
            "From the processed-event table: one row for this event id however many copies arrived. "
            + "The reservation is the corroborating detail - a second application would have tried "
            + "to confirm an already-Confirmed reservation, which the domain refuses outright, so a "
            + "single Confirmed row is evidence that the transition ran once.",
            [
                Check(
                    "Both deliveries were answered 200",
                    "200 and 200",
                    string.Join(" and ", deliveries.Select(delivery => delivery.StatusCode)),
                    deliveries.All(delivery => delivery.StatusCode == StatusCodes.Status200OK)),
                Check(
                    "Exactly one delivery claimed to have moved the order",
                    "1 settled, 1 duplicate (or 2 duplicates if the shop's own dispatcher won)",
                    $"{applied} settled, {duplicates} duplicate",
                    applied + duplicates == 2 && applied <= 1),
                Check(
                    "Times this event id appears in processed_webhook_events",
                    "1",
                    settlement?.TimesApplied.ToString(CultureInfo.InvariantCulture) ?? "(no settlement row)",
                    settlement?.TimesApplied == 1),
                Check(
                    "The order is Paid, once",
                    "Paid, captured equals total",
                    snapshot is null
                        ? "(order unreadable)"
                        : $"{snapshot.Status}, captured {snapshot.Captured.Display} of {snapshot.Total.Display}",
                    snapshot is { Status: nameof(OrderStatus.Paid) }
                    && snapshot.Captured.Amount == snapshot.Total.Amount),
                Check(
                    "One reservation, Confirmed once",
                    "1 Confirmed",
                    Describe(evidence.Reservations, row => row.Status == nameof(ReservationStatus.Confirmed)),
                    evidence.Reservations.Count == 1
                    && evidence.Reservations[0].Status == nameof(ReservationStatus.Confirmed)),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Scenario: the six payment scenarios.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One checkout per simulator scenario, so the whole table can be seen answering at once.
    /// <para>
    /// Honest about its limit: the three asynchronous scenarios are shown at the moment of
    /// checkout, with their notifications sitting in the outbox and their delivery times visible.
    /// Following one to settlement is what the two settlement scenarios do, and waiting for three
    /// of them here would spend the run's whole budget watching a timer.
    /// </para>
    /// </summary>
    private static async Task<LabOutcome> RunPaymentScenariosAsync(LabRun run)
    {
        var kettle = run.Fixture.Variants[0];
        var scenarios = PaymentScenarioCatalog.Descriptors.Select(descriptor => descriptor.Scenario).ToList();

        var shoppers = await run.NewShoppersAsync(scenarios.Count);

        for (var index = 0; index < scenarios.Count; index++)
        {
            await run.SendAsync(run.AddToCart(shoppers.All[index], kettle.VariantId, 1));
        }

        run.Note(
            "Six shoppers, one per gateway behaviour",
            "Each buys one unit and asks the simulated gateway for a different scenario by name. The "
            + "hint is the documented way to select one; the fallback is the amount's trailing "
            + "cents, which is why this run's fixture is priced at a round $45.00 - a price ending "
            + "in .03 would duplicate its webhooks whether or not anybody asked.");

        var results = new List<(PaymentSimulatorScenario Scenario, DemoLabExchange Exchange)>();

        for (var index = 0; index < scenarios.Count; index++)
        {
            var scenario = scenarios[index];

            var exchange = await run.SendAsync(
                run.Checkout(shoppers.All[index], $"lab-{run.RunId}-{scenario}", scenario: scenario.ToString()));

            results.Add((scenario, exchange));

            var descriptor = PaymentScenarioCatalog.Descriptors.First(entry => entry.Scenario == scenario);
            var asynchronous = exchange.StatusCode == StatusCodes.Status202Accepted;

            run.Record(
                $"{scenario}",
                $"{descriptor.Demonstrates} Expected authorization result: {descriptor.AuthorizationResult}. "
                + $"Webhooks it schedules: {descriptor.Webhooks}.",
                exchange,
                fidelity: asynchronous ? NotFollowed : Genuine,
                fidelityNote: asynchronous
                    ? "The checkout half of this scenario is genuine and complete. Its settlement is "
                      + "enqueued in the outbox and delivered later by the shop's dispatcher; this "
                      + "run does not wait for it. The settlement-replay and settlement-race "
                      + "scenarios follow one all the way through."
                    : null);
        }

        run.Caveat(
            "Three of the six settle asynchronously. Their outbox rows are listed in the evidence "
            + "with the earliest instant each may be delivered, so what was promised is visible even "
            + "though this run did not stay to watch it happen.");

        var evidence = await run.EvidenceAsync();
        var ledger = evidence.Ledger[0];

        var succeeded = Answer(results, PaymentSimulatorScenario.Succeed);
        var declined = Answer(results, PaymentSimulatorScenario.Decline);
        var deferred = results
            .Where(result => result.Scenario is PaymentSimulatorScenario.Duplicate
                or PaymentSimulatorScenario.Delay
                or PaymentSimulatorScenario.Reorder)
            .ToList();

        var duplicateRows = evidence.Settlements.Count(settlement =>
            string.Equals(settlement.MessageType, PaymentSettlementEvent.SucceededType, StringComparison.Ordinal));

        return new LabOutcome(
            "Every scenario answered with a defined status rather than an exception, and the three "
            + "deferred ones left signed notifications in the outbox - written in the same "
            + "transaction that persisted the order, which is what makes \"the payment was "
            + "authorized\" and \"a settlement will arrive\" the same fact rather than two hopeful "
            + "ones.",
            [
                Check(
                    "Succeed captured inside the checkout request",
                    "201",
                    succeeded?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "(no answer)",
                    succeeded?.StatusCode == StatusCodes.Status201Created),
                Check(
                    "Decline answered as a business outcome",
                    "402",
                    declined?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "(no answer)",
                    declined?.StatusCode == StatusCodes.Status402PaymentRequired),
                Check(
                    "The deferred scenarios answered 202 rather than blocking",
                    "3 x 202",
                    string.Join(", ", deferred.Select(result => $"{result.Scenario} {result.Exchange.StatusCode}")),
                    deferred.All(result => result.Exchange.StatusCode == StatusCodes.Status202Accepted)),
                Check(
                    "Nothing answered 5xx",
                    "no server errors",
                    $"{results.Count(result => result.Exchange.StatusCode >= 500)} of {results.Count}",
                    results.All(result => result.Exchange.StatusCode is > 0 and < 500)),
                Check(
                    "Settlement notifications were enqueued for the deferred scenarios",
                    "at least 3 (Duplicate enqueues two copies of one event)",
                    duplicateRows.ToString(CultureInfo.InvariantCulture),
                    duplicateRows >= 3),
                Check(
                    "Stock reserved matches the orders that survived",
                    "one unit per order that was not declined",
                    $"{ledger.ReservedAfter} reserved of {ledger.OnHandAfter} on hand",
                    ledger.ReservedAfter == evidence.Orders.Count(order => order.Status != nameof(OrderStatus.Cancelled))),
            ]);

        static DemoLabExchange? Answer(
            List<(PaymentSimulatorScenario Scenario, DemoLabExchange Exchange)> results,
            PaymentSimulatorScenario scenario) =>
            results.FirstOrDefault(result => result.Scenario == scenario).Exchange;
    }

    // ---------------------------------------------------------------------------------------
    // The fixture: created, measured, destroyed.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Seeds one private product with the variants a scenario needs, and a stock ledger for each.
    /// <para>
    /// A real product with a real variant and a real stock row, because that is the only kind a
    /// cart will accept: the cart's variant lookup inner-joins the product, so a soft-deleted one
    /// would be invisible to the catalog and equally unbuyable. The fixture is therefore genuinely
    /// live for the length of the run, and genuinely gone afterwards - which is stated in the
    /// response rather than glossed over.
    /// </para>
    /// </summary>
    private static async Task<LabFixture> SeedFixtureAsync(
        VelaCommerceDbContext db,
        string runId,
        IReadOnlyList<(string Name, int OnHand)> blueprint,
        CancellationToken cancellationToken)
    {
        var product = new Product(
            $"demo-lab-{runId}",
            "Demo Lab fixture",
            "Created by one Demo Lab run to race against, and deleted by the same request. If you "
            + "are reading this in a catalog response, a run is in flight right now.",
            FixtureCategory);

        var variants = new List<LabFixtureVariant>(blueprint.Count);

        for (var index = 0; index < blueprint.Count; index++)
        {
            var (name, onHand) = blueprint[index];

            var variant = product.AddVariant(
                $"LAB-{runId.ToUpperInvariant()}-{index + 1}",
                name,
                new Money(FixturePriceMinorUnits));

            variants.Add(new LabFixtureVariant(variant.Id, variant.Sku, name, FixturePriceMinorUnits, onHand));
        }

        db.Products.Add(product);

        foreach (var variant in variants)
        {
            db.StockItems.Add(new StockItem(variant.VariantId, variant.OnHand));
        }

        await db.SaveChangesAsync(cancellationToken);

        // The tracked fixture entities are not wanted for the rest of the run: every read below is
        // AsNoTracking and every write goes through HTTP, so leaving them in the change tracker
        // would only risk a later SaveChanges picking up something nobody meant to save.
        db.ChangeTracker.Clear();

        return new LabFixture(product.Id, product.Slug, variants);
    }

    /// <summary>The stock ledger for a set of variants, read outside every visitor's session.</summary>
    private static async Task<IReadOnlyDictionary<Guid, LabLedgerReading>> LedgerAsync(
        VelaCommerceDbContext db,
        IReadOnlyList<Guid> variantIds,
        CancellationToken cancellationToken) =>
        await db.StockItems
            .AsNoTracking()
            .Where(stock => variantIds.Contains(stock.VariantId))
            .Select(stock => new { stock.VariantId, stock.OnHand, stock.Reserved })
            .ToDictionaryAsync(
                row => row.VariantId,
                row => new LabLedgerReading(row.OnHand, row.Reserved),
                cancellationToken);

    /// <summary>
    /// Removes everything the run created, and then checks that it did.
    /// <para>
    /// <b>Every statement is scoped by the fixture ids minted in this request</b>, which is what
    /// licenses suppressing the query filters. The rows belong to throwaway sessions, so the
    /// DemoTenancy filter - which fails closed, and correctly shows this request only its own data
    /// - would match nothing and delete nothing. Filters are suppressed entirely rather than by
    /// name because a teardown that could not see a soft-deleted row could not delete the fixture
    /// it hangs off, and would leave a product in the catalog forever.
    /// </para>
    /// <para>
    /// Order matters where a foreign key does. Order lines, cart lines and product variants cascade
    /// from their parents; stock items and reservations have no foreign key to a variant and must
    /// be removed explicitly. The outbox and the processed-event ledger reference nothing at all,
    /// which is deliberate in their design and convenient here.
    /// </para>
    /// <para>
    /// Failures are logged and reported, never thrown. This runs in a <c>finally</c>, and an
    /// exception escaping it would replace a completed run's transcript with a 500.
    /// </para>
    /// </summary>
    private static async Task<LabTeardown> DestroyFixtureAsync(
        VelaCommerceDbContext db,
        LabFixture fixture,
        IReadOnlyCollection<Guid> fixtureSessionIds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var removed = new List<LabRowsRemovedResponse>();
        var variantIds = fixture.VariantIds;

        // Rows belonging to somebody who is not this run. Reported, never silently absorbed:
        // a non-zero here is the shared catalog being touched, which is the one thing the
        // blast-radius block exists to rule out.
        var foreignOrdersPreserved = 0;
        var foreignCartLinesRemoved = 0;

        try
        {
            var orders = await db.Orders
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(order => order.Lines.Any(line => variantIds.Contains(line.VariantId)))
                .Select(order => new { order.Id, order.OrderNumber, order.DemoSessionId })
                .ToListAsync(cancellationToken);

            var orderIds = orders.Select(order => order.Id).ToList();
            var orderNumbers = orders.Select(order => order.OrderNumber).ToList();

            // The sessions that bought something.
            //
            // AN EARLIER VERSION OF THIS COMMENT CLAIMED THESE COULD ONLY EVER BE THE THROWAWAY
            // VISITORS THIS RUN MINTED. THAT WAS FALSE, AND IT DESTROYED REAL DATA. The fixture
            // product is live in the public catalog API for as long as the run takes, so a real
            // shopper CAN add it to a cart or buy it — a reviewer measured thousands of sightings
            // under load, then watched a real Paid order deleted and a real cart lose an unrelated
            // line. Rows are now partitioned by owner: this run's own sessions are removed whole,
            // and anything belonging to somebody else keeps its parent row and loses only the
            // lines that reference the fixture.
            var sessionIds = orders.Select(order => order.DemoSessionId).Distinct().ToList();

            var processed = await db.Set<ProcessedWebhookEvent>()
                .IgnoreQueryFilters()
                .Where(entry => entry.OrderReference != null && orderNumbers.Contains(entry.OrderReference))
                .ExecuteDeleteAsync(cancellationToken);

            removed.Add(new LabRowsRemovedResponse("processed_webhook_events", processed));

            // One statement per order number rather than one clever one: an outbox message holds no
            // foreign key to its order by design, so the only link is the order reference inside the
            // signed payload, and a per-number LIKE is something PostgreSQL can plan. There are
            // never more than a handful of orders in a run.
            var outbox = 0;

            foreach (var orderNumber in orderNumbers)
            {
                outbox += await db.Set<OutboxMessage>()
                    .IgnoreQueryFilters()
                    .Where(message => message.Payload.Contains(orderNumber))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            removed.Add(new LabRowsRemovedResponse("outbox_messages", outbox));

            var reservations = await db.StockReservations
                .IgnoreQueryFilters()
                .Where(reservation => variantIds.Contains(reservation.VariantId))
                .ExecuteDeleteAsync(cancellationToken);

            removed.Add(new LabRowsRemovedResponse("stock_reservations", reservations));

            // Partition by owner. `fixtureSessionIds` are the visitors this run minted; anything
            // else is a real shopper who happened to buy the fixture while it was on sale.
            var foreignOrderIds = orders
                .Where(order => !fixtureSessionIds.Contains(order.DemoSessionId))
                .Select(order => order.Id)
                .ToList();

            var ownOrderIds = orderIds.Except(foreignOrderIds).ToList();

            var deletedOrders = await db.Orders
                .IgnoreQueryFilters()
                .Where(order => ownOrderIds.Contains(order.Id))
                .ExecuteDeleteAsync(cancellationToken);

            // A real shopper's order is never deleted. Its lines reference a variant that is about
            // to disappear, so the order becomes a record of something no longer in the catalog —
            // which is exactly what an order is for. Deleting it would 404 their retrieval link
            // forever, and they would have no idea why.
            foreignOrdersPreserved = foreignOrderIds.Count;

            if (foreignOrdersPreserved > 0)
            {
                logger.LogWarning(
                    "Demo Lab teardown left {Count} order(s) belonging to real visitors intact. They "
                    + "bought a fixture product while it was briefly live in the catalog.",
                    foreignOrdersPreserved);
            }

            removed.Add(new LabRowsRemovedResponse("orders (lines cascade)", deletedOrders));

            // Two sets, because a successful checkout empties the cart it bought from. Matching only
            // on "has a line for the fixture" would therefore miss every WINNER's cart and leave one
            // empty row per sale behind - owned by a session that no longer exists, invisible to
            // everybody, and still debris. The second set closes that: carts belonging to a
            // fixture-buying session that now hold nothing at all. Restricting it to empty carts is
            // what keeps it safe - it can never take a line a real shopper is still using.
            var cartIds = await db.Carts
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(cart => cart.Lines.Any(line => variantIds.Contains(line.VariantId))
                               || (sessionIds.Contains(cart.DemoSessionId) && !cart.Lines.Any()))
                .Select(cart => cart.Id)
                .ToListAsync(cancellationToken);

            // Same partition. A real shopper's cart keeps its row and its unrelated items; only
            // the lines pointing at the fixture go. Deleting the parent took an innocent line with
            // it, which a reviewer reproduced: a real two-line cart came back holding nothing.
            // CartLine carries a CartId and no navigation back to its cart, so the foreign carts
            // are resolved first and the lines deleted by that id.
            var foreignCartIds = await db.Carts
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(cart => cartIds.Contains(cart.Id) && !fixtureSessionIds.Contains(cart.DemoSessionId))
                .Select(cart => cart.Id)
                .ToListAsync(cancellationToken);

            var foreignCartLines = foreignCartIds.Count == 0
                ? 0
                : await db.Set<CartLine>()
                    .IgnoreQueryFilters()
                    .Where(line => foreignCartIds.Contains(line.CartId) && variantIds.Contains(line.VariantId))
                    .ExecuteDeleteAsync(cancellationToken);

            if (foreignCartLines > 0)
            {
                foreignCartLinesRemoved = foreignCartLines;
                logger.LogWarning(
                    "Demo Lab teardown removed {Count} fixture line(s) from real visitors' carts, "
                    + "leaving those carts and their other items intact.",
                    foreignCartLines);
            }

            var ownCartIds = await db.Carts
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(cart => cartIds.Contains(cart.Id) && fixtureSessionIds.Contains(cart.DemoSessionId))
                .Select(cart => cart.Id)
                .ToListAsync(cancellationToken);

            var carts = await db.Carts
                .IgnoreQueryFilters()
                .Where(cart => ownCartIds.Contains(cart.Id))
                .ExecuteDeleteAsync(cancellationToken);

            removed.Add(new LabRowsRemovedResponse("carts (lines cascade)", carts));

            var stock = await db.StockItems
                .IgnoreQueryFilters()
                .Where(item => variantIds.Contains(item.VariantId))
                .ExecuteDeleteAsync(cancellationToken);

            removed.Add(new LabRowsRemovedResponse("stock_items", stock));

            var products = await db.Products
                .IgnoreQueryFilters()
                .Where(product => product.Id == fixture.ProductId)
                .ExecuteDeleteAsync(cancellationToken);

            removed.Add(new LabRowsRemovedResponse("products (variants cascade)", products));

            // Verified rather than assumed. A delete that ran is not the same claim as a fixture
            // that is gone, and the difference would be a product left in the shop's catalog.
            var survivors = await db.ProductVariants
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(variant => variantIds.Contains(variant.Id), cancellationToken);

            var strandedStock = await db.StockItems
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(item => variantIds.Contains(item.VariantId), cancellationToken);

            var clean = survivors == 0 && strandedStock == 0;

            if (!clean)
            {
                logger.LogWarning(
                    "Demo Lab fixture {Slug} did not fully tear down: {Variants} variant(s) and "
                    + "{Stock} stock row(s) remain.",
                    fixture.Slug,
                    survivors,
                    strandedStock);
            }

            var sharedTouched = foreignOrdersPreserved + foreignCartLinesRemoved;

            var sharedNote = sharedTouched == 0
                ? null
                : $"While this run's fixture was briefly listed, {foreignOrdersPreserved} real order(s) "
                  + $"were bought against it and have been LEFT INTACT, and {foreignCartLinesRemoved} "
                  + "fixture line(s) were removed from real visitors' carts, leaving those carts and "
                  + "their other items alone.";

            var debrisNote = clean
                ? null
                : $"{survivors} fixture variant(s) and {strandedStock} stock row(s) survived the "
                  + "teardown. They belong to no visitor and sell nothing, but they are debris "
                  + "and this deployment's log has the details.";

            return new LabTeardown(
                clean,
                removed,
                string.Join(" ", new[] { debrisNote, sharedNote }.Where(note => note is not null)) is { Length: > 0 } note
                    ? note
                    : null,
                sharedTouched);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Demo Lab could not tear down fixture {Slug}. Rows may remain in the catalog.",
                fixture.Slug);

            return new LabTeardown(
                false,
                removed,
                $"The teardown failed with {exception.GetType().Name} after removing "
                + $"{removed.Sum(entry => entry.Rows)} row(s). The fixture product {fixture.Slug} may "
                + "still exist; it is inert, but it is not supposed to be there.",
                foreignOrdersPreserved + foreignCartLinesRemoved);
        }
    }

    private static LabBlastRadiusResponse BlastRadiusOf(LabTeardown teardown) =>
        new(
            "Seed and destroy: the run creates its own product, variant and stock, and deletes all "
            + "of it before answering.",
            "The alternatives were borrowing real inventory and restoring it afterwards - which "
            + "depends on the restore step actually running, and denies real shoppers the stock "
            + "meanwhile - or a permanent lab variant, which would accumulate every order anybody "
            + "ever raced against it. Seeding is the only one where a run that dies halfway cannot "
            + "leave the shop's shelf wrong. Its honest cost: for the second or so a run lasts, one "
            + "fixture product is visible to the catalog API. It is not visible in the storefront, "
            + "which browses from a static snapshot, and it is named so that anybody who does see it "
            + "knows what it is.",
            teardown.SharedRowsTouched,
            teardown.Clean,
            teardown.Removed,
            teardown.Warning);

    private static LabFixtureResponse FixtureView(
        LabFixture? fixture,
        IReadOnlyDictionary<Guid, LabLedgerReading> before)
    {
        const string Why =
            "Private to this run. The shared catalog is never sold from, reserved against or edited "
            + "- a public button that consumed real inventory would empty the shelf for every other "
            + "visitor.";

        if (fixture is null)
        {
            return new LabFixtureResponse("(no fixture was created)", [], Why);
        }

        var variants = fixture.Variants
            .Select(variant => new LabFixtureVariantResponse(
                variant.VariantId,
                variant.Sku,
                variant.DisplayName,
                new MoneyDto(variant.UnitPrice, Money.DefaultCurrency),
                before.TryGetValue(variant.VariantId, out var reading) ? reading.OnHand : variant.OnHand))
            .ToList();

        return new LabFixtureResponse(fixture.Slug, variants, Why);
    }

    // ---------------------------------------------------------------------------------------
    // Small readers, shared by the scenarios.
    // ---------------------------------------------------------------------------------------

    private static LabCheckResponse Check(string claim, string expected, string actual, bool passed) =>
        new(claim, expected, actual, passed);

    private static string Describe<T>(IReadOnlyList<T> rows, Func<T, bool> matches) =>
        rows.Count == 0
            ? "(none)"
            : $"{rows.Count(matches)} of {rows.Count}";

    /// <summary>Reads a field out of a settlement acknowledgement body.</summary>
    private static string? AcknowledgementField(DemoLabExchange exchange, string field) =>
        JsonField(exchange.ResponseBody, field)?.GetString();

    /// <summary>Reads the order number out of any checkout response that carries one.</summary>
    private static string? OrderNumberOf(DemoLabExchange? exchange) =>
        exchange is null ? null : JsonField(exchange.ResponseBody, "orderNumber")?.GetString();

    /// <summary>Units in a cart response, for the "the cart survived" assertion.</summary>
    private static int? CartQuantityOf(DemoLabExchange exchange) =>
        JsonField(exchange.ResponseBody, "totalQuantity") is { } value && value.TryGetInt32(out var quantity)
            ? quantity
            : null;

    /// <summary>The shortfall extension a 409 carries, or null when the body has none.</summary>
    private static LabShortfall? ShortfallOf(DemoLabExchange? exchange)
    {
        if (exchange is null || JsonField(exchange.ResponseBody, "shortfall") is not { } shortfall)
        {
            return null;
        }

        return new LabShortfall(
            shortfall.TryGetProperty("sku", out var sku) ? sku.GetString() : null,
            shortfall.TryGetProperty("requested", out var requested) && requested.TryGetInt32(out var wanted)
                ? wanted
                : null,
            shortfall.TryGetProperty("available", out var available)
            && available.ValueKind is JsonValueKind.Number
            && available.TryGetInt32(out var free)
                ? free
                : null);
    }

    /// <summary>
    /// One top-level property of a JSON body, or null if the body is not JSON, is truncated, or
    /// has no such property. Never throws: every caller is reading a response that may have been
    /// a problem document, an empty body or a truncated one.
    /// </summary>
    private static JsonElement? JsonField(string body, string name)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                ? value.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Where to call, and what to say when we cannot.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Works out the origin to call, preferring the loopback address this host is already
    /// listening on.
    /// <para>
    /// Loopback first for two reasons: it does not leave the machine, and the shop's per-address
    /// rate limiter deliberately exempts loopback - so fifty lab shoppers share no bucket with the
    /// real visitors whose address the request arrived from. HTTP is preferred over HTTPS for the
    /// reason the outbox gives: a loopback call to the development certificate fails validation on
    /// a machine that has not trusted it, which would look like a shop that is down.
    /// </para>
    /// <para>
    /// The request's own origin is the last resort. It is correct and it works, but behind a
    /// reverse proxy it leaves and re-enters the network, and it carries the real client's address
    /// into the limiter.
    /// </para>
    /// </summary>
    private static Uri? ResolveOrigin(HttpContext http, IServiceProvider services)
    {
        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;

        if (addresses is { Count: > 0 })
        {
            var candidates = addresses
                .Select(Normalize)
                .Where(uri => uri is not null)
                .Select(uri => uri!)
                .OrderBy(uri => uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        // The outbox has already solved "where am I listening" for the settlement receiver, so its
        // resolved URL is a better second guess than anything reinvented here.
        if (services.GetService<OutboxOptions>()?.ReceiverUrl is { } receiver)
        {
            return new Uri(receiver.GetLeftPart(UriPartial.Authority));
        }

        return http.Request.Host.HasValue
            ? new Uri($"{http.Request.Scheme}://{http.Request.Host.Value}")
            : null;

        static Uri? Normalize(string address)
        {
            // Kestrel reports wildcards as http://*:8080, http://+:8080 or http://[::]:8080. None
            // of those is a host anything can connect to, and all of them mean "this machine".
            var replaced = address
                .Replace("://*:", "://localhost:", StringComparison.Ordinal)
                .Replace("://+:", "://localhost:", StringComparison.Ordinal)
                .Replace("://[::]:", "://localhost:", StringComparison.Ordinal)
                .Replace("://0.0.0.0:", "://localhost:", StringComparison.Ordinal);

            return Uri.TryCreate(replaced, UriKind.Absolute, out var uri)
                   && uri.Scheme is "http" or "https"
                ? new Uri(uri.GetLeftPart(UriPartial.Authority))
                : null;
        }
    }

    private static ProblemHttpResult UnknownScenarioProblem(string scenarioId) =>
        TypedResults.Problem(
            title: "No such lab scenario",
            detail: $"'{scenarioId}' is not a scenario. The ones that exist are: "
                    + string.Join(", ", DemoLabScenarioCatalog.Ids)
                    + $". GET {RouteGroup}/scenarios describes each of them.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult NotComposedProblem() =>
        TypedResults.Problem(
            title: "The Demo Lab is not composed",
            detail: "This host mapped the lab's endpoints but never registered its services. Add "
                    + "builder.Services.AddDemoLab(builder.Configuration) to the composition root. "
                    + "Answered as a 503 rather than a 500 because nothing is broken - a dependency "
                    + "is simply absent, and the catalogue endpoint still works.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult NoDemoSessionProblem() =>
        TypedResults.Problem(
            title: "No demo session",
            detail: "The lab rate-limits by visitor, and this request arrived without a session "
                    + "bound. Unreachable in the composed host, where the session middleware runs "
                    + "before every endpoint - written down rather than assumed, because a lab that "
                    + "invented an identity here would be a lab with no rate limit.",
            statusCode: StatusCodes.Status500InternalServerError);

    private static ProblemHttpResult NoOriginProblem() =>
        TypedResults.Problem(
            title: "The lab cannot find the shop",
            detail: "No listening address could be resolved, so there is nowhere to send the "
                    + "scenario's requests. This is the state a host with no server bound is in - "
                    + "the build-time OpenAPI generator, for instance - and it is reported rather "
                    + "than guessed at, because posting a run into the void would produce a "
                    + "transcript full of connection failures and no explanation.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult BusyProblem(HttpContext http, DemoLabAdmission admission)
    {
        if (admission.RetryAfterSeconds > 0)
        {
            http.Response.Headers.RetryAfter =
                admission.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(
            title: "The lab is busy",
            detail: admission.Refusal ?? "This run cannot start right now.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    // ---------------------------------------------------------------------------------------
    // The run's own state.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One run in progress: the transcript being written, the shoppers being driven, and the
    /// database handle the evidence is read through.
    /// <para>
    /// A nested class rather than a service, deliberately. It holds the request's
    /// <c>DbContext</c>, and the unit of work belongs to the endpoint that owns the request -
    /// which is the rule the architecture suite enforces by confining the context to persistence,
    /// seeding, the endpoint classes and the composition root.
    /// </para>
    /// </summary>
    private sealed class LabRun
    {
        /// <summary>What a run with nothing to report hands back, so a failure still has a shape.</summary>
        public static LabEvidenceResponse EmptyEvidence { get; } = new(
            [],
            [],
            [],
            [],
            new LabBlastRadiusResponse("unknown", "The run did not get far enough to say.", 0, false, [], null));

        private readonly DemoLabLoopback _loopback;
        private readonly DemoLabOptions _options;
        private readonly Uri _origin;
        private readonly IDataProtectionProvider _dataProtection;

        /// <summary>
        /// Every session this run minted. Teardown removes rows owned by these and leaves
        /// everybody else's alone — ownership is known from the sealed cookie, not inferred.
        /// </summary>
        private readonly HashSet<Guid> _sessionIds = [];
        private readonly List<LabStepResponse> _steps = [];
        private readonly List<string> _caveats = [];
        private readonly Dictionary<string, string?> _rowVersions = new(StringComparer.Ordinal);

        public LabRun(
            VelaCommerceDbContext db,
            DemoLabLoopback loopback,
            DemoLabOptions options,
            Uri origin,
            string runId,
            IDataProtectionProvider dataProtection,
            CancellationToken token)
        {
            Database = db;
            _loopback = loopback;
            _options = options;
            _origin = origin;
            RunId = runId;
            _dataProtection = dataProtection;
            Token = token;
        }

        /// <summary>The sessions this run created, for teardown to scope by.</summary>
        public IReadOnlyCollection<Guid> SessionIds => _sessionIds;

        public VelaCommerceDbContext Database { get; }

        public string RunId { get; }

        public CancellationToken Token { get; }

        public LabFixture Fixture { get; set; } = null!;

        public IReadOnlyDictionary<Guid, LabLedgerReading> LedgerBefore { get; set; } =
            new Dictionary<Guid, LabLedgerReading>();

        public IReadOnlyList<LabStepResponse> Steps => _steps;

        public IReadOnlyList<string> Caveats => _caveats;

        public LabEvidenceResponse? Evidence { get; private set; }

        public LabTeardown Teardown { get; set; } = LabTeardown.NothingToDo;

        /// <summary>Clamps a requested participant count to the configured ceiling.</summary>
        public int Clamp(int requested) => Math.Clamp(requested, 2, _options.MaxParticipants);

        public void Caveat(string text) => _caveats.Add(text);

        /// <summary>A step with no HTTP: commentary that carries the argument between exchanges.</summary>
        public void Note(string title, string narration) =>
            _steps.Add(new LabStepResponse(
                _steps.Count + 1,
                title,
                narration,
                null,
                null,
                null,
                Concurrency: 1,
                Represents: 1,
                Fidelity: Genuine,
                FidelityNote: null));

        /// <summary>A step that shows one real exchange.</summary>
        public void Record(
            string title,
            string narration,
            DemoLabExchange exchange,
            int concurrency = 1,
            int represents = 1,
            string fidelity = Genuine,
            string? fidelityNote = null) =>
            _steps.Add(new LabStepResponse(
                _steps.Count + 1,
                title,
                exchange.Transport is null
                    ? narration
                    : narration + $" (No response: {exchange.Transport})",
                new LabRequestResponse(
                    exchange.Method,
                    exchange.Path,
                    [.. exchange.RequestHeaders.Select(header => new LabHeaderResponse(header.Name, header.Value))],
                    exchange.RequestBody),
                new LabResponseResponse(
                    exchange.StatusCode,
                    exchange.ReasonPhrase,
                    [.. exchange.ResponseHeaders.Select(header => new LabHeaderResponse(header.Name, header.Value))],
                    exchange.ResponseBody,
                    exchange.Transport),
                exchange.ElapsedMilliseconds,
                concurrency,
                represents,
                fidelity,
                fidelityNote));

        public Task<DemoLabExchange> SendAsync(DemoLabRequest request) =>
            _loopback.SendAsync(_origin, request, Token);

        // -- the requests a shopper makes ----------------------------------------------------

        public DemoLabRequest Handshake() =>
            new(HttpMethod.Get, "/api/cart", DemoSessionMiddleware.CookieName);

        public DemoLabRequest Cart(LabShopper shopper) =>
            new(HttpMethod.Get, "/api/cart", DemoSessionMiddleware.CookieName, shopper.Cookie);

        public DemoLabRequest AddToCart(LabShopper shopper, Guid variantId, int quantity) =>
            new(
                HttpMethod.Post,
                "/api/cart/items",
                DemoSessionMiddleware.CookieName,
                shopper.Cookie,
                JsonSerializer.SerializeToUtf8Bytes(new CartAddItemRequest(variantId, quantity), Wire));

        /// <summary>
        /// A checkout, with the payment outcome pinned rather than left to chance.
        /// <para>
        /// <b>Why <c>Succeed</c> is the default instead of no hint at all.</b> With no hint the
        /// simulator selects a scenario from the trailing cents of the ORDER TOTAL - not the unit
        /// price - and the total carries shipping and tax, so it lands on every value of cents. A
        /// stock scenario that let the amount decide would decline or defer roughly one checkout
        /// in twenty for reasons that have nothing to do with stock, and the transcript would show
        /// a race apparently losing units to a payment failure. The hint exists precisely to be
        /// unambiguous, it is visible in every request body printed below, and the scenario that
        /// is actually about payments passes its own.
        /// </para>
        /// </summary>
        public DemoLabRequest Checkout(LabShopper shopper, string idempotencyKey, string? scenario = null) =>
            new(
                HttpMethod.Post,
                "/api/checkout",
                DemoSessionMiddleware.CookieName,
                shopper.Cookie,
                JsonSerializer.SerializeToUtf8Bytes(
                    new CheckoutRequest(
                        FixtureAddress,
                        IdempotencyKey: null,
                        PaymentScenario: scenario ?? nameof(PaymentSimulatorScenario.Succeed)),
                    Wire),
                Headers: [new DemoLabHeader("Idempotency-Key", idempotencyKey)]);

        /// <summary>
        /// A settlement delivery, exactly as the dispatcher makes it: the stored bytes and the
        /// stored signature header, neither re-serialized nor re-signed.
        /// </summary>
        public DemoLabRequest Deliver(LabNotification notification) =>
            new(
                HttpMethod.Post,
                WebhookEndpoints.SettlementRoute,
                DemoSessionMiddleware.CookieName,
                SessionCookie: null,
                Body: notification.Payload,
                ContentType: "application/json",
                Headers: [new DemoLabHeader(PaymentSignature.HeaderName, notification.SignatureHeader)]);

        // -- shoppers ------------------------------------------------------------------------

        /// <summary>One new visitor, with a session the shop minted.</summary>
        public async Task<LabShopper> NewShopperAsync()
        {
            var exchange = await SendAsync(Handshake());

            if (DemoSessionMiddleware.TryReadSessionId(_dataProtection, exchange.IssuedSessionCookie, out var sessionId))
            {
                _sessionIds.Add(sessionId);
            }

            return new LabShopper(exchange.IssuedSessionCookie, sessionId);
        }

        /// <summary>
        /// A crowd of new visitors, all arriving at once. Their handshakes are returned so a
        /// scenario can print one of them and say how many it stood for.
        /// </summary>
        public async Task<LabCrowd> NewShoppersAsync(int count)
        {
            var handshakes = await AllAtOnceAsync(count, _ => SendAsync(Handshake()));
            var shoppers = handshakes
                .Select(exchange =>
                {
                    // Same as NewShopperAsync: ownership is recorded at creation, so teardown
                    // never has to guess whose row it is looking at.
                    if (DemoSessionMiddleware.TryReadSessionId(_dataProtection, exchange.IssuedSessionCookie, out var id))
                    {
                        _sessionIds.Add(id);
                    }

                    return new LabShopper(exchange.IssuedSessionCookie, id);
                })
                .ToArray();

            var strangers = shoppers.Count(shopper => shopper.Cookie is null);

            if (strangers > 0)
            {
                Caveat(
                    $"{strangers} of {count} shoppers never received a session cookie, so their "
                    + "later requests would have arrived as new visitors with empty carts. The "
                    + "counts below include them, and this is why the run's numbers may not add up.");
            }

            return new LabCrowd(shoppers, handshakes);
        }

        /// <summary>
        /// Builds every attempt first, parks them on one gate, and releases them together.
        /// <para>
        /// This is what makes a race a race. Started in a loop, the first request would usually be
        /// finished before the second was written, and the scenario would prove only that the shop
        /// can handle one thing at a time - which is the failure mode a concurrency demonstration
        /// can least afford, because it looks exactly like success.
        /// </para>
        /// </summary>
        public static async Task<T[]> AllAtOnceAsync<T>(int count, Func<int, Task<T>> attempt)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var attempts = Enumerable.Range(0, count)
                .Select(index => Task.Run(async () =>
                {
                    await gate.Task;
                    return await attempt(index);
                }))
                .ToArray();

            gate.SetResult();

            return await Task.WhenAll(attempts);
        }

        // -- the deferred-settlement setup the two webhook scenarios share --------------------

        /// <summary>
        /// Buys one unit under the <c>Delay</c> scenario and reads back the signed notification
        /// checkout enqueued for it.
        /// </summary>
        /// <returns>
        /// The order number and the notification, or a sentence explaining why the scenario cannot
        /// continue. A missing outbox row is not an exception here - it is a finding.
        /// </returns>
        public async Task<(string? OrderNumber, LabNotification? Notification, string Why)> PlaceDeferredOrderAsync(
            string label)
        {
            var variant = Fixture.Variants[0];
            var shopper = await NewShopperAsync();

            await SendAsync(AddToCart(shopper, variant.VariantId, 1));

            var placed = await SendAsync(
                Checkout(shopper, $"lab-{RunId}-{label}", scenario: nameof(PaymentSimulatorScenario.Delay)));

            Record(
                "A shopper checks out, and the gateway says it will settle later",
                "202 Accepted, not 201: the payment was authorized but not captured, so the order is "
                + "Pending and the storefront's job is to say \"confirming payment\" rather than "
                + "spin. In the SAME transaction that persisted the order, checkout wrote a signed "
                + "settlement notification to the outbox - which is what makes \"the payment was "
                + "authorized\" and \"a webhook will arrive\" one fact instead of two hopeful ones.",
                placed);

            if (OrderNumberOf(placed) is not { } orderNumber)
            {
                return (null, null, "The checkout did not return an order number, so there is no "
                                    + "settlement to deliver. The transcript above shows what it answered instead.");
            }

            var messages = await Database.Set<OutboxMessage>()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(message => message.Payload.Contains(orderNumber))
                .OrderBy(message => message.DeliverAfter)
                .ThenBy(message => message.Id)
                .Select(message => new { message.Payload, message.SignatureHeader, message.MessageType })
                .ToListAsync(Token);

            if (messages.Count == 0)
            {
                return (orderNumber, null, $"Order {orderNumber} was created but no settlement "
                                           + "notification was enqueued for it, which would itself be a broken invariant.");
            }

            var message = messages[0];

            Note(
                "The gateway's own bytes, read back out of the outbox",
                $"The notification is a {message.MessageType} carrying the order reference, and it "
                + "is delivered below EXACTLY as stored: the same octets and the same "
                + "X-Vela-Signature that was computed over them. Nothing is re-serialized and "
                + "nothing is re-signed - a redelivery that rebuilt the payload would be a different "
                + "message that happened to say the same thing, and would test content deduplication "
                + "rather than the event-id deduplication a real gateway retry exercises.");

            return (
                orderNumber,
                new LabNotification(Encoding.UTF8.GetBytes(message.Payload), message.SignatureHeader),
                string.Empty);
        }

        // -- evidence ------------------------------------------------------------------------

        /// <summary>
        /// PostgreSQL's <c>xmin</c> for one order: the id of the transaction that last wrote the
        /// row. Recorded as it is read, so the evidence block can show the final value.
        /// </summary>
        public async Task<string?> RowVersionAsync(string orderNumber)
        {
            var versions = await Database.Database
                .SqlQuery<string>($"""SELECT xmin::text AS "Value" FROM orders WHERE order_number = {orderNumber}""")
                .ToListAsync(Token);

            var version = versions.Count == 1 ? versions[0] : null;
            _rowVersions[orderNumber] = version;

            return version;
        }

        /// <summary>One order as the table holds it, read outside every visitor's session.</summary>
        public async Task<LabOrderResponse?> OrderSnapshotAsync(string orderNumber)
        {
            var order = await Database.Orders
                .AsNoTracking()
                .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                .Include(entity => entity.Lines)
                .SingleOrDefaultAsync(entity => entity.OrderNumber == orderNumber, Token);

            return order is null
                ? null
                : new LabOrderResponse(
                    order.OrderNumber,
                    order.Status.ToString(),
                    new MoneyDto(order.Total.Amount, order.Total.Currency),
                    new MoneyDto(order.Captured.Amount, order.Captured.Currency),
                    order.PlacedAt,
                    order.PaidAt,
                    order.Lines.Sum(line => line.Quantity),
                    "visitor-1",
                    _rowVersions.GetValueOrDefault(orderNumber));
        }

        /// <summary>
        /// Everything the database says about this run, gathered once and cached.
        /// <para>
        /// <b>The tenancy filter is suppressed by name, and only that one.</b> These rows belong to
        /// the throwaway sessions the run created, and <c>DemoTenancy</c> fails closed - so a
        /// context bound to the caller's session sees none of them. Counting zero is the right
        /// answer to "what may this visitor see" and a useless answer to "what does the table say",
        /// which is the only question evidence asks. Naming the filter leaves <c>SoftDelete</c> in
        /// place, so a row that was soft-deleted stays hidden here, as it should.
        /// </para>
        /// </summary>
        public async Task<LabEvidenceResponse> EvidenceAsync()
        {
            if (Evidence is not null)
            {
                return Evidence;
            }

            var variantIds = Fixture.VariantIds;
            var ledgerAfter = await LedgerAsync(Database, variantIds, Token);

            var orders = await Database.Orders
                .AsNoTracking()
                .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                .Include(order => order.Lines)
                .Where(order => order.Lines.Any(line => variantIds.Contains(line.VariantId)))
                .OrderBy(order => order.PlacedAt)
                .ThenBy(order => order.OrderNumber)
                .ToListAsync(Token);

            // Visitors are labelled in order of appearance rather than by session id. The assertion
            // that matters is "these five orders belong to five different people"; publishing the
            // ids themselves would name a visitor in a response anybody can fetch.
            var visitors = new Dictionary<Guid, string>();

            foreach (var order in orders)
            {
                if (!visitors.ContainsKey(order.DemoSessionId))
                {
                    visitors[order.DemoSessionId] = $"visitor-{visitors.Count + 1}";
                }
            }

            var orderRows = orders
                .Select(order => new LabOrderResponse(
                    order.OrderNumber,
                    order.Status.ToString(),
                    new MoneyDto(order.Total.Amount, order.Total.Currency),
                    new MoneyDto(order.Captured.Amount, order.Captured.Currency),
                    order.PlacedAt,
                    order.PaidAt,
                    order.Lines.Sum(line => line.Quantity),
                    visitors[order.DemoSessionId],
                    _rowVersions.GetValueOrDefault(order.OrderNumber)))
                .ToList();

            var numbers = orders.ToDictionary(order => order.Id, order => order.OrderNumber);

            var reservationRows = await Database.StockReservations
                .AsNoTracking()
                .Where(reservation => variantIds.Contains(reservation.VariantId))
                .OrderBy(reservation => reservation.Id)
                .Select(reservation => new
                {
                    reservation.VariantId,
                    reservation.OrderId,
                    reservation.Quantity,
                    reservation.Status,
                })
                .ToListAsync(Token);

            var skus = Fixture.Variants.ToDictionary(variant => variant.VariantId, variant => variant.Sku);

            var reservations = reservationRows
                .Select(row => new LabReservationResponse(
                    skus.GetValueOrDefault(row.VariantId, "(unknown)"),
                    numbers.GetValueOrDefault(row.OrderId, "(order removed)"),
                    row.Quantity,
                    row.Status.ToString()))
                .ToList();

            var settlements = await SettlementsAsync(numbers.Values);

            var ledger = Fixture.Variants
                .Select(variant =>
                {
                    var before = LedgerBefore.GetValueOrDefault(variant.VariantId, new LabLedgerReading(variant.OnHand, 0));
                    var after = ledgerAfter.GetValueOrDefault(variant.VariantId, before);

                    return new LabLedgerResponse(
                        variant.Sku,
                        variant.DisplayName,
                        before.OnHand,
                        before.Reserved,
                        before.OnHand - before.Reserved,
                        after.OnHand,
                        after.Reserved,
                        after.OnHand - after.Reserved);
                })
                .ToList();

            Evidence = new LabEvidenceResponse(
                orderRows,
                ledger,
                reservations,
                settlements,
                // Filled in by the caller once the teardown has actually run, because "what did this
                // touch" is not answerable until the cleaning up is done.
                new LabBlastRadiusResponse("pending", "The teardown had not run when this was read.", 0, false, [], null));

            return Evidence;
        }

        /// <summary>
        /// The settlement notifications these orders produced, and how many times each event was
        /// actually applied.
        /// </summary>
        private async Task<IReadOnlyList<LabSettlementResponse>> SettlementsAsync(IEnumerable<string> orderNumbers)
        {
            var settlements = new List<LabSettlementResponse>();
            var pending = new List<(string OrderNumber, string EventId, string MessageType, string Status, int Attempts, DateTimeOffset DeliverAfter, string Signature)>();

            foreach (var orderNumber in orderNumbers)
            {
                var messages = await Database.Set<OutboxMessage>()
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(message => message.Payload.Contains(orderNumber))
                    .OrderBy(message => message.DeliverAfter)
                    .ThenBy(message => message.Id)
                    .Select(message => new
                    {
                        message.MessageType,
                        message.Payload,
                        message.SignatureHeader,
                        message.Status,
                        message.Attempts,
                        message.DeliverAfter,
                    })
                    .ToListAsync(Token);

                foreach (var message in messages)
                {
                    // Read with the options the payload was written with, and only to learn the
                    // event id. Never to re-send: a re-serialization is a different message from the
                    // one that was signed.
                    var settlement = JsonSerializer.Deserialize<PaymentSettlementEvent>(
                        message.Payload,
                        PaymentSettlementEvent.SerializerOptions);

                    pending.Add((
                        orderNumber,
                        settlement?.EventId ?? "(unreadable)",
                        message.MessageType,
                        message.Status.ToString(),
                        message.Attempts,
                        message.DeliverAfter,
                        message.SignatureHeader));
                }
            }

            if (pending.Count == 0)
            {
                return settlements;
            }

            var eventIds = pending.Select(entry => entry.EventId).Distinct(StringComparer.Ordinal).ToList();

            var appliedCounts = await Database.Set<ProcessedWebhookEvent>()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(processed => eventIds.Contains(processed.EventId))
                .GroupBy(processed => processed.EventId)
                .Select(group => new { EventId = group.Key, Count = group.Count() })
                .ToListAsync(Token);

            var applied = appliedCounts.ToDictionary(entry => entry.EventId, entry => entry.Count, StringComparer.Ordinal);

            foreach (var entry in pending)
            {
                settlements.Add(new LabSettlementResponse(
                    entry.OrderNumber,
                    entry.MessageType,
                    entry.EventId,
                    entry.Status,
                    entry.Attempts,
                    entry.DeliverAfter,
                    entry.Signature,
                    applied.GetValueOrDefault(entry.EventId, 0)));
            }

            return settlements;
        }
    }

    // ---------------------------------------------------------------------------------------
    // The run's small value types.
    // ---------------------------------------------------------------------------------------

    /// <summary>One throwaway visitor: a sealed session cookie and nothing else.</summary>
    /// <summary>
    /// One throwaway visitor. <paramref name="SessionId"/> is recovered from the sealed cookie at
    /// creation, so teardown can tell this run's rows from a real shopper's by identity rather
    /// than by guessing — the guess is what deleted somebody's paid order.
    /// </summary>
    private sealed record LabShopper(string? Cookie, Guid SessionId);

    /// <summary>A group of visitors that arrived together, with the handshakes that made them.</summary>
    private sealed record LabCrowd(IReadOnlyList<LabShopper> All, IReadOnlyList<DemoLabExchange> Handshakes);

    /// <summary>The exact bytes and header of one enqueued settlement.</summary>
    private sealed record LabNotification(byte[] Payload, string SignatureHeader);

    /// <summary>One variant this run seeded for itself.</summary>
    private sealed record LabFixtureVariant(
        Guid VariantId,
        string Sku,
        string DisplayName,
        long UnitPrice,
        int OnHand);

    /// <summary>The private product a run races against.</summary>
    private sealed record LabFixture(Guid ProductId, string Slug, IReadOnlyList<LabFixtureVariant> Variants)
    {
        /// <summary>The ids every teardown statement is scoped by.</summary>
        public IReadOnlyList<Guid> VariantIds { get; } =
            [.. Variants.Select(variant => variant.VariantId)];
    }

    /// <summary>The two numbers the stock argument is about, at one instant.</summary>
    private readonly record struct LabLedgerReading(int OnHand, int Reserved);

    /// <summary>What the teardown removed, and whether anything survived it.</summary>
    /// <param name="SharedRowsTouched">
    /// Rows that did NOT belong to this run: a real visitor's order left intact, or a fixture line
    /// removed from their cart. It is a measurement, not a constant. It was a hardcoded zero once,
    /// and it printed zero on the very runs that were deleting real people's paid orders — the one
    /// number whose job was to catch that could not see it.
    /// </param>
    private sealed record LabTeardown(
        bool Clean,
        IReadOnlyList<LabRowsRemovedResponse> Removed,
        string? Warning,
        int SharedRowsTouched = 0)
    {
        /// <summary>The teardown of a run that never created anything.</summary>
        public static LabTeardown NothingToDo { get; } = new(true, [], null);
    }

    /// <summary>The shortfall a 409 carries, flattened for assertions.</summary>
    private sealed record LabShortfall(string? Sku, int? Requested, int? Available);

    /// <summary>A scenario's conclusion: the reasoning, and the comparisons behind it.</summary>
    private sealed record LabOutcome(string HowWeKnow, IReadOnlyList<LabCheckResponse> Checks)
    {
        /// <summary>
        /// A run that could not reach a conclusion. Deliberately carries no checks, so the verdict
        /// reads as "did not hold" rather than as a pass - a lab that could only produce good news
        /// would produce it against a broken shop too.
        /// </summary>
        public static LabOutcome Inconclusive(string why) => new(why, []);
    }
}
