using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Scalar.AspNetCore;
using VelaCommerce.Api.Admin;
using VelaCommerce.Api.Endpoints;
using VelaCommerce.Api.Hosting;
using VelaCommerce.Api.Tenancy;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.DemoLab;
using VelaCommerce.Infrastructure.Fulfilment;
using VelaCommerce.Infrastructure.Messaging;
using VelaCommerce.Infrastructure.Payments;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Seeding;
using VelaCommerce.Infrastructure.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// The catalog path must answer while everything else is cold, so the host stays lean:
// no session, no auth on the read path, and nothing expensive in the DI graph.
builder.Services.AddOpenApi();

builder.Services.AddDbContext<VelaCommerceDbContext>(options =>
{
    var connectionString =
        Environment.GetEnvironmentVariable("VELA_DB_CONNECTION")
        ?? builder.Configuration.GetConnectionString("Vela")
        ?? throw new InvalidOperationException(
            "No database connection string. Set VELA_DB_CONNECTION or ConnectionStrings:Vela.");

    // A duplicate settlement delivery is EXPECTED: the receiver deliberately lets the insert
    // into processed_webhook_events lose on the primary key rather than checking first, because
    // check-then-insert is the race this design exists to avoid. EF logs that failure and two
    // stack traces at Error before the handler ever sees it, so the headline "Duplicate" demo
    // scenario printed a page of alarm for a mechanism working correctly.
    options.ConfigureWarnings(warnings =>
        warnings.Log((CoreEventId.SaveChangesFailed, LogLevel.Debug)));

    options.UseNpgsql(connectionString, npgsql =>
    {
        // Retry only on transient faults: a serverless Postgres may be resuming.
        npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
    });
});

builder.Services.AddProblemDetails();

// Model-binding failures are the caller's fault, so they must read as 400 in every
// environment. Left at its default this throws in Development only, and the exception
// handler turns that into a 500 — so a typo in a request body looked like a server bug
// locally and in CI, but not in production.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddScoped<CatalogSeeder>();

// Checkout registers TimeProvider, which ASP.NET Core does not provide and the handler
// needs because reading the ambient clock is banned by an architecture test.
builder.Services.AddCheckout(builder.Configuration);

// The simulator is the DEFAULT gateway on purpose: this repo has to clone and complete a
// purchase with no payment account and no network. The environment flag is what makes
// AssertUsable refuse the committed development signing secret outside Development - on the
// money path, when a payment or refund is attempted, NOT at startup. An earlier version of
// this comment said "refuse to start", which it has never done; the same overstatement was
// corrected in eight other places and this one survived.
builder.Services.AddPaymentSimulator(builder.Configuration, builder.Environment.IsDevelopment());

// The outbox makes "the payment was authorized" and "a settlement webhook will arrive" the
// same fact: the notification is written in the transaction that persists the order, so one
// cannot happen without the other. Takes root configuration because it discovers the
// receiver's address from the host's own urls.
builder.Services.AddOutbox(builder.Configuration);

// Advances paid orders through Packed and Shipped on a demo clock, so a reviewer watches the
// lifecycle in a minute rather than a week.
builder.Services.AddOrderTimeline(builder.Configuration);

// Identity on a site with no accounts: a signed cookie, and a scoped holder the DbContext reads
// when it filters carts and orders. Registering it here is what turns tenancy on — and forgetting
// to would hide every cart rather than share them, which is the only acceptable direction for that
// mistake to fail in.
builder.Services.AddDemoSessionTenancy();

// Rate limits, per-session row caps and security headers. A public demo left unattended needs
// all three: one visitor must not be able to fill the database or spend the whole request budget.
builder.Services.AddDemoSafety(builder.Configuration);

// The demo admin: a second cookie asserting a binding to the caller's own demo session, and an
// authorization policy that checks the binding on every admin route. It gates the console, not the
// data — every admin query runs through the same tenancy-filtered sets the shop does.
builder.Services.AddDemoAdmin(requireSecureCookie: !builder.Environment.IsDevelopment());

// Static SSR only — no AddInteractiveServerComponents, and no render mode anywhere in the admin
// area. An interactive component needs a live SignalR circuit, and this host is meant to scale to
// zero: a console whose buttons stopped working once the container slept would be worse than one
// that reloads a page. It also keeps the CSP as it is, because nothing here executes script.
builder.Services.AddRazorComponents();

// Read-side projections for the admin pages, so no Razor component ever holds a DbContext.
builder.Services.AddScoped<AdminPageData>();

// The Demo Lab: a reviewer presses a button and watches an invariant hold, against the same
// code paths a real purchase uses. Public and unauthenticated, so it seeds its own private
// stock rather than selling the shared catalog's, and is bounded by a run budget that is not
// keyed on session — a session is free to mint, so a per-visitor cooldown alone is farmable.
builder.Services.AddDemoLab(builder.Configuration);

// DATA PROTECTION KEYS MUST OUTLIVE THE CONTAINER.
//
// Everything a visitor holds is sealed with this key ring: the demo-session cookie that IS
// their identity, and the signed token on an order-retrieval link. The default ring lives in
// the container's filesystem, so on Container Apps every deploy — and every scale from zero —
// would mint a new one, silently emptying every cart and 404-ing every order link. The
// symptom looks like a bug in the cart, which is the worst kind.
//
// SetApplicationName matters as much as the blob: without it the ring is namespaced by the
// entry assembly name, so a project rename invalidates every cookie exactly the same way.
var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("vela-commerce");

// The blob is opt-in by its environment variable, and its absence is NOT an error. Two hosts
// legitimately run without it: a developer's machine, and the build-time OpenAPI generator,
// which executes this entry point as Production. Throwing here would break the build rather
// than the deployment — the same trap the payment simulator's startup guard already fell into.
var keyRingBlobUri = builder.Configuration["VELA_DATAPROTECTION_BLOB_URI"]
                     ?? Environment.GetEnvironmentVariable("VELA_DATAPROTECTION_BLOB_URI");

Uri? keyRingUri = null;

if (!string.IsNullOrWhiteSpace(keyRingBlobUri))
{
    if (!Uri.TryCreate(keyRingBlobUri, UriKind.Absolute, out keyRingUri))
    {
        throw new InvalidOperationException(
            $"VELA_DATAPROTECTION_BLOB_URI is set to '{keyRingBlobUri}', which is not an absolute "
            + "URI. A misspelt blob URI must not be mistaken for an unset one: falling back to an "
            + "ephemeral key ring would log every visitor out on the next deploy and look like a "
            + "bug in the cart.");
    }

    // DefaultAzureCredential resolves the container app's managed identity in Azure and a
    // developer's az login locally, so the same line works in both without a secret.
    dataProtection.PersistKeysToAzureBlobStorage(keyRingUri, new DefaultAzureCredential());
}

var usingBlobKeyRing = keyRingUri is not null;
// Warned about below, once the logger exists — deliberately NOT written to stderr here. The
// build-time OpenAPI generator runs this entry point and treats anything on stderr as a build
// error, so the warning about a misconfigured deployment would have broken the build instead.
var keyRingIsEphemeral = !usingBlobKeyRing && !builder.Environment.IsDevelopment();

var app = builder.Build();

if (keyRingIsEphemeral)
{
    // Loud, but not fatal. A host without a shared ring still serves; it just logs everybody
    // out on the next deploy, and that deserves to be findable in a log rather than discovered
    // by a shopper whose cart emptied itself.
    app.Logger.LogWarning(
        "VELA_DATAPROTECTION_BLOB_URI is not set outside Development. Data Protection keys will "
        + "live in the container filesystem, so every deploy and every scale from zero will "
        + "invalidate every session cookie and every order-retrieval link. See infra/dataprotection.tf.");
}

// Development convenience only: migrate and seed so a fresh clone is browsable in one
// command. Production applies migrations as a separate one-shot job, never at startup,
// so a slow migration cannot fail the container's health probe.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();
    await db.Database.MigrateAsync();

    // Located rather than computed: the repository path this used to hard-code resolves to
    // nothing inside a container, where the file sits beside the assembly instead. A host that
    // seeds itself has to work in both layouts, so CatalogSeedFile probes them in order and says
    // what it looked at when it finds neither.
    if (CatalogSeedFile.Locate(app.Environment, app.Configuration, app.Logger) is { } seedFile)
    {
        await scope.ServiceProvider.GetRequiredService<CatalogSeeder>().SeedAsync(seedFile);
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Before the endpoints, and after the error handlers so that a failure while establishing the
// session still comes back as ProblemDetails. Every request downstream of this line has a demo
// session bound; every request upstream of it — and every code path that never sees a request at
// all — has none, and the query filter shows such a caller nothing rather than everything.
app.UseDemoSession();

// Position is load-bearing: the limiter partitions by demo session, so it has to run after the
// session is bound and before anything it protects.
app.UseDemoSafety();

// AFTER UseDemoSession, and that ordering is the policy's precondition rather than a preference:
// BoundToTheCallersSessionHandler compares the admin ticket against ICurrentDemoSession, so the
// session must already be bound when authorization runs. Before it, every admin request would be
// refused for want of a session the middleware had not got to yet.
app.UseAuthentication();
app.UseAuthorization();

// Scalar is Development-only, per Microsoft's guidance for the built-in OpenAPI document.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// BEFORE the API groups is fine and before MapStorefront is essential: the storefront installs an
// SPA fallback that would otherwise answer /admin with the shop's shell.
//
// UseAntiforgery is not optional once this is mapped. MapRazorComponents stamps antiforgery
// metadata on every endpoint it creates, and the endpoint middleware throws at request time — a 500
// on the very first GET /admin — if the middleware that consumes that metadata was never added.
app.UseAntiforgery();
app.MapRazorComponents<VelaCommerce.Api.Admin.App>();

app.MapCatalogEndpoints();
app.MapCartEndpoints();
app.MapCheckoutEndpoints();

// Refunds and cancellation. Mapped as its own group rather than onto the checkout's, because the
// two differ on who may call them: a checkout response's signed retrieval token opens the order for
// reading, and deliberately does not open these - a forwarded receipt link must not carry the power
// to move the money it describes.
app.MapRefundEndpoints();
app.MapWebhookEndpoints();
app.MapDemoEndpoints();
app.MapDemoLabEndpoints();

// The admin console's writes. Mounted under /api so the demo rate limiter, which partitions on
// that prefix, covers them by construction rather than by a second list of routes.
app.MapAdminEndpoints();

// Two separate probes: liveness must never touch the database, or a sleeping
// database would get the container killed rather than merely reported unhealthy.
app.MapGet("/alive", () => TypedResults.Ok(new { status = "alive" }))
   .WithName("Liveness")
   .WithSummary("Process is up. Deliberately does not touch the database.")
   .ExcludeFromDescription();

app.MapGet("/health", async (VelaCommerceDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "healthy", database = "reachable" })
        : Results.Problem(title: "Database unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
})
.WithName("Readiness")
.WithSummary("Readiness probe: verifies the database is reachable.");

// LAST, deliberately. This installs static files plus an SPA fallback so a deep link like
// /p/some-slug survives a refresh. Mapped before the API routes it would swallow them, and
// the shop would answer index.html to every fetch.
app.MapStorefront();

app.Run();

/// <summary>Exposed so the integration test project can drive the host with WebApplicationFactory.</summary>
public partial class Program;
