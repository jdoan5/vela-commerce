using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using VelaCommerce.Api.Endpoints;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Seeding;

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
builder.Services.AddScoped<CatalogSeeder>();

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

// Scalar is Development-only, per Microsoft's guidance for the built-in OpenAPI document.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapCatalogEndpoints();

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
