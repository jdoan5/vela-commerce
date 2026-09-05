using Microsoft.EntityFrameworkCore;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Catalog;
using VelaCommerce.Domain.Inventory;
using VelaCommerce.Domain.Orders;
using VelaCommerce.Infrastructure.Tenancy;

using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;

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
/// Every ENTITY type carries a query filter named <c>SoftDelete</c>. Three tables do not, and each
/// says so in its own configuration: outbox_messages and processed_webhook_events are message logs
/// rather than entities, and demo_catalog_price_overrides is a per-session overlay where deleting a
/// row already means what soft-deleting it would. A caller that genuinely
/// needs deleted rows — an admin audit view, a restore — asks for them explicitly with
/// <c>IgnoreQueryFilters(["SoftDelete"])</c>, which leaves any filter added later (demo
/// tenancy, for instance) still in force.
/// </para>
/// <para>
/// <see cref="Carts"/>, <see cref="Orders"/> and the admin's per-session price overlay carry a
/// second filter named <c>DemoTenancy</c>, applied in <see cref="OnModelCreating"/>. It is the
/// reason a forgotten <c>WHERE</c> clause in one endpoint cannot show one visitor another
/// visitor's cart: the restriction is a property of the model, not of the query somebody
/// remembered to write.
/// </para>
/// </summary>
public sealed class VelaCommerceDbContext : DbContext
{
    /// <summary>
    /// The name of the tenancy filter, for the rare caller that must stand outside it — the
    /// nightly demo reset, an operator's diagnostic. Spelled <c>IgnoreQueryFilters(["DemoTenancy"])</c>
    /// so that opting out of tenancy is a visible, greppable act that still leaves
    /// <c>SoftDelete</c> in place.
    /// </summary>
    public const string DemoTenancyFilter = "DemoTenancy";

    private readonly ICurrentDemoSession? _demoSession;

    /// <summary>
    /// Takes the session as an <em>accessor</em>, never as a value.
    /// <para>
    /// The DI scope and the context are usually created before the middleware has read the cookie,
    /// so a captured <see cref="Guid"/> would be stale — and stale here means either "no rows" or,
    /// far worse, "the previous request's rows". Holding the accessor defers the read to query
    /// translation time, when the answer is known.
    /// </para>
    /// <para>
    /// The parameter is optional so that the design-time factory, migrations and the test fixtures
    /// can still say <c>new VelaCommerceDbContext(options)</c>. That is safe precisely because a
    /// missing accessor means "no session", and no session matches no rows.
    /// </para>
    /// </summary>
    public VelaCommerceDbContext(
        DbContextOptions<VelaCommerceDbContext> options,
        ICurrentDemoSession? demoSession = null)
        : base(options)
    {
        _demoSession = demoSession;
    }

    /// <summary>
    /// The visitor this context is answering for, or <see langword="null"/> when there is none.
    /// <para>
    /// Public and instance-level for a mechanical reason, not a stylistic one. EF caches the model
    /// — including the compiled shape of every query filter — once per context type. A filter that
    /// closed over a local variable would bake one visitor's id into that cached model and serve it
    /// to everybody. A filter that reads an instance member of the context is rewritten by EF into
    /// an access on the <em>current</em> context and lifted into a SQL parameter, so one cached
    /// plan serves every visitor with their own id bound at execution.
    /// </para>
    /// </summary>
    public Guid? CurrentDemoSessionId => _demoSession?.SessionId;

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

        ApplyDemoTenancy(modelBuilder);
    }

    /// <summary>
    /// Adds the <c>DemoTenancy</c> filter to the three entities that carry a <c>DemoSessionId</c>.
    /// <para>
    /// It lives here rather than in <c>CartConfiguration</c> / <c>OrderConfiguration</c> because the
    /// predicate has to reference an instance member of <em>this</em> context (see
    /// <see cref="CurrentDemoSessionId"/>), and an <see cref="IEntityTypeConfiguration{TEntity}"/>
    /// discovered by assembly scan has no way to reach one. Both configuration files point here.
    /// </para>
    /// <para>
    /// <strong>Fail closed.</strong> The predicate is written as "there is a session, AND the row
    /// belongs to it", so the null case collapses to <c>false</c> and returns nothing. The obvious
    /// alternative — "no session, therefore no restriction" — reads more naturally and is exactly
    /// backwards: it turns every path that forgot to establish a session (a background job, a
    /// misordered middleware, a test harness, a request that 500s before the cookie is read) into a
    /// full-table read of every visitor's carts and orders. The direction of the default is the
    /// whole security property, so it is stated explicitly rather than left to SQL's three-valued
    /// logic to arrive at by accident.
    /// </para>
    /// <para>
    /// Naming the filter matters as much as writing it: because <c>SoftDelete</c> and
    /// <c>DemoTenancy</c> are separate named filters, a caller that suppresses one keeps the other.
    /// A single anonymous filter would mean any admin query for deleted rows silently became a
    /// cross-visitor query too.
    /// </para>
    /// <para>
    /// Scope is the aggregate roots, plus the overlay, which is a root in its own right — it has
    /// no parent to be reached through, so it carries its own session id and its own filter.
    /// <c>CartLine</c> and <c>OrderLine</c> hold no session id of
    /// their own and are reached through their parent, which is filtered; querying
    /// <c>Set&lt;CartLine&gt;()</c> directly would side-step tenancy, which is why neither is
    /// exposed as a <see cref="DbSet{TEntity}"/> and why line queries belong on the root.
    /// </para>
    /// </summary>
    private void ApplyDemoTenancy(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Cart>().HasQueryFilter(
            DemoTenancyFilter,
            cart => CurrentDemoSessionId != null && cart.DemoSessionId == CurrentDemoSessionId);

        modelBuilder.Entity<Order>().HasQueryFilter(
            DemoTenancyFilter,
            order => CurrentDemoSessionId != null && order.DemoSessionId == CurrentDemoSessionId);

        // The price overlay. Its fail-closed direction is worth naming because it is the benign
        // one: a caller with no session matches no override rows, so the resolution falls through
        // to the shared seed price. A visitor without a session sees the shop's own prices — never
        // somebody else's, and never a blank catalog.
        modelBuilder.Entity<DemoCatalogPriceOverride>().HasQueryFilter(
            DemoTenancyFilter,
            over => CurrentDemoSessionId != null && over.DemoSessionId == CurrentDemoSessionId);
    }
}
