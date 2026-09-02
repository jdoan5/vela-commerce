using Microsoft.EntityFrameworkCore;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;

namespace VelaCommerce.Infrastructure.Persistence;

/// <summary>
/// The single unit of work over PostgreSQL.
/// <para>
/// Mapping lives in <see cref="IEntityTypeConfiguration{TEntity}"/> classes rather than in this
/// file, so each aggregate's storage decisions sit next to one another and this type stays a
/// readable manifest of what the application can reach.
/// </para>
/// <para>
/// Only aggregate roots (plus <see cref="ProductVariants"/>, which carts, stock and orders
/// address directly by id) get a <see cref="DbSet{TEntity}"/>. Lines are reached through their
/// parent so that quantity and total invariants stay in the domain instead of leaking into
/// query code.
/// </para>
/// <para>
/// Every mapped type carries a query filter named <c>SoftDelete</c>. A caller that genuinely
/// needs deleted rows — an admin audit view, a restore — asks for them explicitly with
/// <c>IgnoreQueryFilters(["SoftDelete"])</c>, which leaves any filter added later (demo
/// tenancy, for instance) still in force.
/// </para>
/// </summary>
public sealed class VelaCommerceDbContext : DbContext
{
    public VelaCommerceDbContext(DbContextOptions<VelaCommerceDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();

    /// <summary>Exposed on its own because stock, cart lines and order lines reference a variant by id.</summary>
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    public DbSet<Cart> Carts => Set<Cart>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VelaCommerceDbContext).Assembly);
    }
}
