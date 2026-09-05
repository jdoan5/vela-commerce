namespace VelaCommerce.Infrastructure.Persistence.CatalogOverrides;

/// <summary>
/// One visitor's private price for one variant, laid over a catalog nobody may write.
/// <para>
/// The demo admin has to be able to reprice things — it is the operation that makes an admin look
/// like an admin, and the one that gives the checkout's price-changed guard a trigger a reviewer
/// can pull. It also cannot be allowed to reprice the shop, because the shop is shared with every
/// other visitor and the seeded catalog is generated deterministically and asserted byte-identical
/// by CI. So an admin's price lands here, keyed by the session that set it, and the seed row is
/// never touched.
/// </para>
/// <para>
/// <b>Deliberately not an <see cref="VelaCommerce.Domain.Common.Entity"/>.</b> It has no identity
/// of its own — the pair that owns it IS the key — and no soft delete, because a stale override is
/// meaningless rather than historic: clearing one restores the shared price, which is exactly what
/// deleting the row does. Following <c>ProcessedWebhookEvent</c>, which is an infrastructure record
/// rather than a domain concept for the same reasons.
/// </para>
/// <para>
/// <b>No currency column, on purpose.</b> The currency comes from the seed variant, so "an override
/// cannot change a product's currency" is unrepresentable here rather than checked somewhere and
/// hoped for. <b>No foreign key to product_variants</b> either, matching the stock ledger's own
/// note: the domain models that link as a bare id with no navigation, and adding one here would be
/// the only place in the schema that disagreed.
/// </para>
/// </summary>
// internal, and load-bearing. EffectiveCatalogPrices claims to be the only place that reads or
// writes this table; the claim was prose for one day, the admin console's page reader broke it, and
// the fix was not to write the sentence more firmly. Outside Infrastructure the compiler now says
// no. Inside it, CatalogOverlayRules does.
internal sealed class DemoCatalogPriceOverride
{
    private DemoCatalogPriceOverride() { } // EF

    public DemoCatalogPriceOverride(Guid demoSessionId, Guid variantId, long priceAmount, DateTimeOffset now)
    {
        if (demoSessionId == Guid.Empty)
            throw new ArgumentException("An override must belong to a session.", nameof(demoSessionId));
        if (priceAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(priceAmount), priceAmount, "A price cannot be negative.");

        DemoSessionId = demoSessionId;
        VariantId = variantId;
        PriceAmount = priceAmount;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Who set it. Half of the primary key, and what the tenancy filter matches on.</summary>
    public Guid DemoSessionId { get; private set; }

    /// <summary>Which variant it covers. The other half of the key.</summary>
    public Guid VariantId { get; private set; }

    /// <summary>The price this visitor sees, in minor units. Never negative; the database agrees.</summary>
    public long PriceAmount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Moves an existing override. Time is a parameter, as it is everywhere else here.</summary>
    public void MoveTo(long priceAmount, DateTimeOffset now)
    {
        if (priceAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(priceAmount), priceAmount, "A price cannot be negative.");

        PriceAmount = priceAmount;
        UpdatedAt = now;
    }
}
