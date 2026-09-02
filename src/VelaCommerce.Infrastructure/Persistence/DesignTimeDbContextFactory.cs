using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VelaCommerce.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> run against this project alone.
/// <para>
/// Without it the tooling has to boot the API host to find a context, which drags
/// configuration, DI and hosted services into a step that only needs a model. Keeping the two
/// apart means a migration can be scaffolded on a laptop with no secrets and no running app.
/// </para>
/// <para>
/// The connection string comes from <c>VELA_DB_CONNECTION</c> so CI and the local Postgres
/// container can each point the tooling somewhere different. The fallback is a throwaway local
/// development database; nothing here is a credential worth protecting, and design time never
/// touches a deployed environment.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VelaCommerceDbContext>
{
    private const string EnvironmentVariableName = "VELA_DB_CONNECTION";

    private const string LocalDevelopmentFallback =
        "Host=localhost;Port=5432;Database=vela_dev";

    public VelaCommerceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariableName) is { Length: > 0 } configured
            ? configured
            : LocalDevelopmentFallback;

        // Pinning the target to PostgreSQL 18 tells the provider which SQL it may emit. It is the
        // floor this schema assumes: native uuid keys that hold the UUIDv7 values the domain
        // generates in .NET, jsonb for product attributes and the shipping address, and generated
        // columns for anything derived we add later. Scaffolding against a lower version would
        // silently produce more conservative DDL than the database we actually run.
        var options = new DbContextOptionsBuilder<VelaCommerceDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
            .Options;

        return new VelaCommerceDbContext(options);
    }
}
