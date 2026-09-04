using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VelaCommerce.Infrastructure.Persistence.CatalogOverrides;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the per-session price overlay.
/// <para>
/// The composite natural key is doing three jobs at once, which is why there is no surrogate id and
/// no second index: it is the read index — every lookup filters on the session, which is the key's
/// leading column — the <c>ON CONFLICT</c> target the bulk reprice upserts against, and the range
/// the demo reset deletes. A generated id would add a column, an index and a question about UUID
/// version, and buy nothing.
/// </para>
/// <para>
/// The tenancy filter is added in <c>VelaCommerceDbContext.OnModelCreating</c> rather than here,
/// for the reason the order and cart configurations give: the predicate reads an instance member of
/// the context so EF parameterises the session per request, and a configuration found by assembly
/// scan has no context to read.
/// </para>
/// </summary>
internal sealed class DemoCatalogPriceOverrideConfiguration : IEntityTypeConfiguration<DemoCatalogPriceOverride>
{
    public void Configure(EntityTypeBuilder<DemoCatalogPriceOverride> builder)
    {
        builder.ToTable("demo_catalog_price_overrides", table =>
        {
            // The all-zero GUID is what "no session" looks like when a variable is left unset, and a
            // row carrying it would read as belonging to whoever was compared against Guid.Empty.
            // Unrepresentable beats filtered — the same reasoning as carts and orders.
            table.HasCheckConstraint(
                "ck_demo_catalog_price_overrides_demo_session_id_present",
                "demo_session_id <> '00000000-0000-0000-0000-000000000000'");

            // A backstop, never the error an admin sees: the reprice clamps and the single-variant
            // override validates first. It is here so a hand-written UPDATE meets the same wall.
            table.HasCheckConstraint(
                "ck_demo_catalog_price_overrides_price_non_negative",
                "price_amount >= 0");
        });

        builder.HasKey(o => new { o.DemoSessionId, o.VariantId })
            .HasName("pk_demo_catalog_price_overrides");

        builder.Property(o => o.DemoSessionId).HasColumnName("demo_session_id");
        builder.Property(o => o.VariantId).HasColumnName("variant_id");
        builder.Property(o => o.PriceAmount).HasColumnName("price_amount");

        builder.Property(o => o.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(o => o.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");

        // NO SoftDelete filter and no deleted_at column, matching outbox_messages and
        // processed_webhook_events. Clearing an override restores the shared price, which is what
        // deleting the row already means; a soft-deleted override would be a row that still had to
        // be excluded from the resolution join forever.
    }
}
