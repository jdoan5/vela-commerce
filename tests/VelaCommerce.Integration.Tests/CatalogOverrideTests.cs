using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Common;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;
using VelaCommerce.Infrastructure.Tenancy;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The per-session price overlay, and the claim the whole admin design rests on.
/// <para>
/// A public demo cannot have an admin that reprices the shop: the catalog is shared with every
/// other visitor and generated deterministically, and CI asserts the seed is byte-identical between
/// runs. So an admin's price lands in a per-session overlay and the seed row is never touched —
/// which is a sentence until something proves it. These are that something.
/// </para>
/// <para>
/// Two of them are the load-bearing ones. <see cref="An_override_is_invisible_to_every_other_session"/>
/// is the isolation claim. <see cref="No_admin_write_ever_touches_a_shared_row"/> is the
/// immutability claim, and it is asserted with PostgreSQL's own <c>xmin</c> — the system column that
/// changes on any row version — over products, product_variants AND stock_items, with no exclusions.
/// A design that had to carve an exception into that assertion would be a design that writes shared
/// rows somewhere.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogOverrideTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    /// <summary>
    /// A context bound to one visitor, activated by the container exactly as a request's would be.
    /// <para>
    /// Constructing the context by hand would give it no <c>ICurrentDemoSession</c>, the tenancy
    /// filter would fail closed on every read, and every assertion below would pass by seeing
    /// nothing. The accessor has to come from the container for these tests to mean anything.
    /// </para>
    /// </summary>
    private VelaCommerceDbContext SessionContext(Guid sessionId, DbCommandInterceptor? interceptor = null)
    {
        var services = new ServiceCollection().AddDemoSessionTenancy();

        services.AddDbContext<VelaCommerceDbContext>(options =>
        {
            options.UseNpgsql(fixture.ConnectionString);

            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        provider.GetRequiredService<IDemoSessionBinder>().Bind(sessionId);

        return provider.GetRequiredService<VelaCommerceDbContext>();
    }

    /// <summary>Seeds a product in its own category, so a category reprice cannot reach anyone else's.</summary>
    private async Task<(Guid VariantId, string Category, long SeedPrice)> SeedAsync(long price = 4_500)
    {
        await using var db = fixture.CreateContext();

        var category = $"overlay-{Guid.CreateVersion7():N}"[..24];
        var product = new Product($"ovr-{Guid.CreateVersion7():N}", "Storm Jib", "Seeded for overlay tests.", category);
        var variant = product.AddVariant($"OVR-{Guid.NewGuid():N}"[..18], "Standard", new Money(price));

        db.Products.Add(product);
        await db.SaveChangesAsync();

        return (variant.Id, category, price);
    }

    private async Task<long> EffectivePriceAsync(Guid sessionId, Guid variantId)
    {
        await using var db = SessionContext(sessionId);
        var resolved = await db.EffectiveVariantAsync(variantId);

        Assert.NotNull(resolved);
        return resolved.PriceAmount;
    }

    [Fact]
    public async Task An_override_is_invisible_to_every_other_session()
    {
        var (variantId, _, seed) = await SeedAsync();

        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();

        await using (var db = SessionContext(alice))
        {
            await db.SetOverrideAsync(alice, variantId, 1_00, Now);
        }

        // THE CLAIM. Alice marked it down to a pound; Bob is shopping in the same catalog.
        Assert.Equal(1_00, await EffectivePriceAsync(alice, variantId));
        Assert.Equal(seed, await EffectivePriceAsync(bob, variantId));

        // And a caller with no session at all falls through to the shared price rather than to
        // Alice's, or to nothing. This is the benign direction of a filter that fails closed.
        await using var sessionless = fixture.CreateContext();
        var anonymous = await sessionless.EffectiveVariantAsync(variantId);

        Assert.NotNull(anonymous);
        Assert.Equal(seed, anonymous.PriceAmount);
    }

    /// <summary>
    /// Every admin write, measured against PostgreSQL's own row-version column.
    /// <para>
    /// <c>xmin</c> is the transaction id that last wrote a row; it changes on any UPDATE, including
    /// one that writes the same value. Capturing it across all three shared tables before and after
    /// the full set of admin operations is the strongest available statement of "the admin never
    /// wrote a shared row" — stronger than comparing prices, which a write-then-write-back would
    /// pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_admin_write_ever_touches_a_shared_row()
    {
        var (variantId, category, _) = await SeedAsync();
        var session = Guid.CreateVersion7();

        var before = await SharedRowVersionsAsync();

        await using (var db = SessionContext(session))
        {
            await db.SetOverrideAsync(session, variantId, 9_99, Now);
            await db.RepriceCategoryAsync(session, category, percent: -20, Now);
            await db.ClearOverridesAsync();
            await db.RepriceCategoryAsync(session, category, percent: 15, Now);
        }

        var after = await SharedRowVersionsAsync();

        Assert.Equal(before, after);
    }

    /// <summary>
    /// The row versions of every table an admin action must not write. Ordered so the comparison is
    /// stable, and read with the filters suppressed so nothing is hidden from the check itself.
    /// </summary>
    private async Task<string> SharedRowVersionsAsync()
    {
        await using var db = fixture.CreateContext();

        var rows = await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT string_agg(entry, ',' ORDER BY entry) AS "Value"
                 FROM (
                     SELECT 'p:' || id || ':' || xmin AS entry FROM products
                     UNION ALL
                     SELECT 'v:' || id || ':' || xmin FROM product_variants
                     UNION ALL
                     SELECT 's:' || id || ':' || xmin FROM stock_items
                 ) AS versions
                 """)
            .SingleAsync();

        return rows;
    }

    /// <summary>
    /// Reads the SQL the bulk reprice actually emits, because the whole design turns on that
    /// statement writing the overlay and nothing else. A reprice that named product_variants would
    /// be the one edit that inverts this feature, and it would look entirely reasonable in a diff.
    /// </summary>
    [Fact]
    public async Task The_bulk_reprice_writes_the_overlay_and_names_no_shared_table()
    {
        var (_, category, _) = await SeedAsync();
        var session = Guid.CreateVersion7();

        var recorder = new CommandRecorder();

        await using (var db = SessionContext(session, recorder))
        {
            await db.RepriceCategoryAsync(session, category, percent: -10, Now);
        }

        var writes = recorder.Commands
            .Where(text => text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(writes);

        foreach (var write in writes)
        {
            Assert.Contains("demo_catalog_price_overrides", write, StringComparison.Ordinal);

            Assert.False(
                write.Contains("UPDATE product_variants", StringComparison.OrdinalIgnoreCase)
                || write.Contains("UPDATE products", StringComparison.OrdinalIgnoreCase)
                || write.Contains("UPDATE stock_items", StringComparison.OrdinalIgnoreCase),
                $"A reprice wrote a shared table. The overlay exists so that it cannot:\n{write}");
        }

        // The tenancy filter, not a hand-written predicate, is what scopes the bulk UPDATE. If this
        // stops appearing, the statement has become genuinely global.
        Assert.Contains(writes, write =>
            write.Contains("UPDATE demo_catalog_price_overrides", StringComparison.OrdinalIgnoreCase)
            && write.Contains("demo_session_id", StringComparison.Ordinal));
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Repricing twice must compound from the current override rather than resetting to seed, and
    /// the arithmetic must truncate the way PostgreSQL's integer division does. Both values are
    /// pinned so that changing the expression has to be a decision.
    /// </summary>
    [Fact]
    public async Task A_reprice_compounds_from_the_current_price_and_truncates()
    {
        var (variantId, category, _) = await SeedAsync(price: 4_499);
        var session = Guid.CreateVersion7();

        await using var db = SessionContext(session);

        await db.RepriceCategoryAsync(session, category, percent: -10, Now);

        // 4499 * 90 / 100 = 4049.1, truncated toward zero.
        Assert.Equal(4_049, await EffectivePriceAsync(session, variantId));

        await db.RepriceCategoryAsync(session, category, percent: -10, Now);

        // Compounded from 4049, not recomputed from the seed: 4049 * 90 / 100 = 3644.1.
        Assert.Equal(3_644, await EffectivePriceAsync(session, variantId));
    }

    [Fact]
    public async Task A_discount_past_free_clamps_at_zero_rather_than_failing_the_statement()
    {
        var (variantId, category, _) = await SeedAsync(price: 1_000);
        var session = Guid.CreateVersion7();

        await using var db = SessionContext(session);

        // The form bounds percent at -50, but the helper is the thing under test and the CHECK
        // constraint is what a negative price would meet - failing the whole bulk statement rather
        // than clamping one row.
        await db.RepriceCategoryAsync(session, category, percent: -150, Now);

        Assert.Equal(0, await EffectivePriceAsync(session, variantId));
    }

    [Fact]
    public async Task Clearing_restores_the_shared_price()
    {
        var (variantId, _, seed) = await SeedAsync();
        var session = Guid.CreateVersion7();

        await using var db = SessionContext(session);

        await db.SetOverrideAsync(session, variantId, 1, Now);
        Assert.Equal(1, await EffectivePriceAsync(session, variantId));

        await db.ClearOverridesAsync();

        // Deleting the row IS restoring the price, which is why the overlay carries no soft delete.
        Assert.Equal(seed, await EffectivePriceAsync(session, variantId));
    }
}
