using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using VelaCommerce.Api.Endpoints;
using VelaCommerce.Api.Tenancy;
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
