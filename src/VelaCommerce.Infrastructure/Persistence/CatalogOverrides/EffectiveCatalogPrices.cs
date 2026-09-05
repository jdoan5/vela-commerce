using Microsoft.EntityFrameworkCore;

namespace VelaCommerce.Infrastructure.Persistence.CatalogOverrides;

/// <summary>
/// What a variant costs <em>this</em> visitor: the shared seed price, unless they have overridden it.
/// <para>
/// <b>This is the only place in the solution that names <see cref="DemoCatalogPriceOverride"/>.</b>
/// That is a deliberate convention rather than an accident of layering, and an architecture test
/// keeps it: price resolution appearing in two places is price resolution that will differ in two
/// places, and the way it differs is that one of them forgets. A cart that captures the seed price
/// and a checkout that compares against the overlay produces an order that can never be placed —
/// the guard fires, the storefront tells the shopper to remove and re-add the line, and re-adding
/// captures the seed price again and re-arms it.
/// </para>
/// <para>
/// <b>Tenancy is never written here.</b> There is no <c>WHERE demo_session_id = …</c> in this file.
/// The overlay carries the <c>DemoTenancy</c> query filter, so the set is already narrowed to the
/// caller before this correlated subquery sees it, and a caller with no session matches nothing and
/// falls through to the seed price. Writing the predicate by hand would work today and would be the
/// thing that stops matching the filter the day the filter changes.
/// </para>
/// <para>
/// The shape is the correlated scalar subquery <c>CatalogEndpoints</c> already uses against the
/// stock ledger, which is why it composes into an existing projection without a join and without
/// changing the row count of the query it is grafted into.
/// </para>
/// </summary>
public static class EffectiveCatalogPrices
{
    /// <summary>
    /// One sellable variant with the price this visitor would pay, or null when there is nothing to
    /// sell — an unknown id, a soft-deleted variant, or a variant whose product has been withdrawn.
    /// <para>
    /// The <c>Product</c> navigation is required rather than decorative: EF inner-joins it, so the
    /// product's own soft-delete filter hides variants of a withdrawn product. Dropping it would
    /// quietly make those addable again.
    /// </para>
    /// </summary>
    public static Task<EffectiveVariant?> EffectiveVariantAsync(
        this VelaCommerceDbContext db,
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.ProductVariants
            .AsNoTracking()
            .Where(v => v.Id == variantId && v.DeletedAt == null)
            .Select(v => new EffectiveVariant(
                v.Id,
                v.Sku,
                v.Name,
                v.Product!.Name,
                db.Set<DemoCatalogPriceOverride>()
                    .Where(o => o.VariantId == v.Id)
                    .Select(o => (long?)o.PriceAmount)
                    .FirstOrDefault() ?? v.Price.Amount,
                v.Price.Currency))
            .FirstOrDefaultAsync(cancellationToken)!;
    }

    /// <summary>
    /// The price this visitor would pay for each of <paramref name="variantIds"/>, keyed by id.
    /// <para>
    /// <b>Absence keeps its existing meaning</b> — the variant is no longer sellable — and the
    /// overlay cannot change that. The resolution is a COALESCE over a row that
    /// <c>product_variants</c> returned, not a union with the override table, so an override for a
    /// withdrawn variant contributes nothing and the caller still reports "there is no price any
    /// more" rather than "the price is unchanged".
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, long>> EffectivePriceAmountsAsync(
        this VelaCommerceDbContext db,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(variantIds);

        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, long>();
        }

        return await db.ProductVariants
            .AsNoTracking()
            .Where(v => variantIds.Contains(v.Id) && v.DeletedAt == null)
            .Select(v => new
            {
                v.Id,
                Amount = db.Set<DemoCatalogPriceOverride>()
                    .Where(o => o.VariantId == v.Id)
                    .Select(o => (long?)o.PriceAmount)
                    .FirstOrDefault() ?? v.Price.Amount,
            })
            .ToDictionaryAsync(row => row.Id, row => row.Amount, cancellationToken);
    }

    // -------------------------------------------------------------------------------------------
    // Writes. Same file as the reads on purpose: the override table has exactly one gateway.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets one variant's price for one session, creating the override or moving an existing one.
    /// <para>
    /// Read-then-write rather than an upsert statement, and safe because of what it is: a single
    /// visitor's private row, written from a form they submitted. There is no second writer to race
    /// — no background worker touches this table — so the composite primary key is a backstop
    /// against a double-submitted form rather than the thing correctness rests on.
    /// </para>
    /// </summary>
    public static async Task SetOverrideAsync(
        this VelaCommerceDbContext db,
        Guid demoSessionId,
        Guid variantId,
        long priceAmount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var existing = await db.Set<DemoCatalogPriceOverride>()
            .FirstOrDefaultAsync(o => o.VariantId == variantId, cancellationToken);

        if (existing is null)
        {
            db.Add(new DemoCatalogPriceOverride(demoSessionId, variantId, priceAmount, now));
        }
        else
        {
            existing.MoveTo(priceAmount, now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Moves every price in a category by a percentage, for this session only.
    /// <para>
    /// <b>This is the operation the admin exists to demonstrate</b>, and the one that looks global.
    /// It is two statements, and the split is the whole point. The first materialises an override
    /// row at the SEED price for every variant in the category that does not have one yet — an
    /// <c>INSERT … SELECT … ON CONFLICT DO NOTHING</c>, so a category repriced twice does not reset
    /// to seed in between. The second is an <c>ExecuteUpdateAsync</c> over the override set, which
    /// the <c>DemoTenancy</c> filter has already narrowed to the caller: a bulk UPDATE that reads
    /// as global and cannot leave this session.
    /// </para>
    /// <para>
    /// <b>The arithmetic truncates, and that is a decision rather than an accident.</b> PostgreSQL
    /// divides integers toward zero, so a 10% markdown of 4,499 minor units yields 4,049 and not
    /// 4,049.1 — rounding in the shopper's favour on a markdown and the shop's on a markup, by at
    /// most one minor unit. Tests pin the values so that a later change to this expression has to be
    /// deliberate. <c>GREATEST(0, …)</c> keeps a large discount from crossing zero and meeting the
    /// non-negative CHECK, which would fail the whole statement rather than clamp one row.
    /// </para>
    /// </summary>
    /// <returns>How many variants this session now has an override for in that category.</returns>
    public static async Task<int> RepriceCategoryAsync(
        this VelaCommerceDbContext db,
        Guid demoSessionId,
        string category,
        int percent,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Materialise at the seed price. Written as SQL because EF cannot express
        // INSERT … SELECT … ON CONFLICT, and because the alternative — reading every variant into
        // memory to add entities — turns one statement into a round trip per category.
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO demo_catalog_price_overrides
                 (demo_session_id, variant_id, price_amount, created_at, updated_at)
             SELECT {demoSessionId}, variant.id, variant.price_amount, {now}, {now}
             FROM product_variants AS variant
             JOIN products AS product ON product.id = variant.product_id
             WHERE product.category = {category}
               AND variant.deleted_at IS NULL
               AND product.deleted_at IS NULL
             ON CONFLICT (demo_session_id, variant_id) DO NOTHING
             """,
            cancellationToken);

        var variantIds = await db.ProductVariants
            .AsNoTracking()
            .Where(v => v.Product!.Category == category && v.DeletedAt == null)
            .Select(v => v.Id)
            .ToArrayAsync(cancellationToken);

        if (variantIds.Length == 0)
        {
            return 0;
        }

        // No WHERE on the session. The filter supplies it, which is exactly what makes this
        // statement safe to write in the shape an admin's bulk operation wants to be written in.
        return await db.Set<DemoCatalogPriceOverride>()
            .Where(o => variantIds.Contains(o.VariantId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(o => o.PriceAmount, o => o.PriceAmount * (100 + percent) / 100 < 0
                        ? 0
                        : o.PriceAmount * (100 + percent) / 100)
                    .SetProperty(o => o.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// Drops this session's overrides, restoring the shared prices. Used by the admin's clear
    /// button and by the demo reset, which must leave a visitor looking like a new one.
    /// </summary>
    public static Task<int> ClearOverridesAsync(
        this VelaCommerceDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.Set<DemoCatalogPriceOverride>().ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// A variant as the cart needs it, priced for the caller.
/// <para>
/// Name and SKU travel with the price because the cart copies all three at capture time — an order
/// has to read correctly a year later whatever has happened to the catalog since.
/// </para>
/// </summary>
/// <param name="Id">The variant.</param>
/// <param name="Sku">As the catalog holds it.</param>
/// <param name="VariantName">The variant's own name, such as a size.</param>
/// <param name="ProductName">The product it belongs to.</param>
/// <param name="PriceAmount">Minor units, after this session's override if it has one.</param>
/// <param name="PriceCurrency">Always the seed row's currency; an override cannot change it.</param>
public sealed record EffectiveVariant(
    Guid Id,
    string Sku,
    string VariantName,
    string ProductName,
    long PriceAmount,
    string PriceCurrency);
