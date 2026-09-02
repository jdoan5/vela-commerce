using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using VelaCommerce.Infrastructure.Persistence;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// Starts a real PostgreSQL 18 in Docker and applies the real migrations.
/// <para>
/// The point of these tests is that the invariants hold in the DATABASE. An in-memory
/// or SQLite provider would happily accept rows that PostgreSQL rejects, which would
/// make the suite worse than useless: green, and wrong about the thing it claims to
/// prove. The image tag is pinned to the same major version production runs.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("vela_test")
        .WithUsername("vela")
        .WithPassword("vela")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public VelaCommerceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<VelaCommerceDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new VelaCommerceDbContext(options);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
