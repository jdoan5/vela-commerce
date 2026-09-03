using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Scalar.AspNetCore;
using VelaCommerce.Api.Endpoints;
using VelaCommerce.Api.Tenancy;
using VelaCommerce.Infrastructure.Checkout;
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
builder.Services.AddCheckout();

// The simulator is the DEFAULT gateway on purpose: this repo has to clone and complete a
// purchase with no payment account and no network. The environment flag makes it refuse to
// start outside Development while the committed development signing secret is in use.
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

// The default key ring is fine locally: keys land under ~/.aspnet/DataProtection-Keys and survive
// a restart, so a session outlives `dotnet run`. Production needs a persisted, shared ring —
// PersistKeysToAzureBlobStorage plus ProtectKeysToAzureKeyVault for a container app — because keys
// generated inside an ephemeral filesystem die with the container and keys generated per-instance
// are not shared across them. The symptom either way is the same and is easy to misread: every
// visitor silently loses their cart on deploy or on a scale-out, because their cookie no longer
// decrypts. Not built now — the demo is a single instance and the phase this belongs to is deploy.
builder.Services.AddDataProtection();

var app = builder.Build();

// Development convenience only: migrate and seed so a fresh clone is browsable in one
// command. Production applies migrations as a separate one-shot job, never at startup,
// so a slow migration cannot fail the container's health probe.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<VelaCommerceDbContext>();
    await db.Database.MigrateAsync();

    var seedFile = Path.Combine(app.Environment.ContentRootPath, "..", "..", "seed", "catalog.seed.json");
    await scope.ServiceProvider.GetRequiredService<CatalogSeeder>().SeedAsync(Path.GetFullPath(seedFile));
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Before the endpoints, and after the error handlers so that a failure while establishing the
// session still comes back as ProblemDetails. Every request downstream of this line has a demo
// session bound; every request upstream of it — and every code path that never sees a request at
// all — has none, and the query filter shows such a caller nothing rather than everything.
app.UseDemoSession();

// Scalar is Development-only, per Microsoft's guidance for the built-in OpenAPI document.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapCatalogEndpoints();
app.MapCartEndpoints();
app.MapCheckoutEndpoints();
app.MapWebhookEndpoints();

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

app.Run();

/// <summary>Exposed so the integration test project can drive the host with WebApplicationFactory.</summary>
public partial class Program;
