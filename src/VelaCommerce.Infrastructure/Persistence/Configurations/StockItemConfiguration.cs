using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VelaCommerce.Domain.Inventory;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the stock ledger for one variant.
/// <para>
/// The check constraints here are the point of this table. Two shoppers can each hold a valid
/// <see cref="StockItem"/> instance that says the last unit is available, and both will pass
/// <see cref="StockItem.TryReserve"/> in memory. <c>reserved &lt;= on_hand</c> is what makes the
/// second write fail instead of overselling, which is why the rule lives in the database and not
/// only in C#.
/// </para>
/// </summary>
internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("stock_items", table =>
        {
            table.HasCheckConstraint("ck_stock_items_on_hand_non_negative", "on_hand >= 0");
            table.HasCheckConstraint("ck_stock_items_reserved_non_negative", "reserved >= 0");
            table.HasCheckConstraint("ck_stock_items_reserved_within_on_hand", "reserved <= on_hand");
        });

        builder.HasKey(stock => stock.Id).HasName("pk_stock_items");

        builder.Property(stock => stock.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(stock => stock.VariantId)
            .HasColumnName("variant_id");

        builder.Property(stock => stock.OnHand)
            .HasColumnName("on_hand");

        builder.Property(stock => stock.Reserved)
            .HasColumnName("reserved");

        builder.Property(stock => stock.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        // Computed as on_hand - reserved by the domain. Storing it would give the two numbers a
        // third chance to disagree.
        builder.Ignore(stock => stock.Available);

        // One ledger row per variant. The uniqueness is what lets a reservation take a row lock on
        // a known single row instead of racing to create a second ledger for the same SKU.
        builder.HasIndex(stock => stock.VariantId)
            .IsUnique()
            .HasDatabaseName("ux_stock_items_variant_id");

        // Deliberately no foreign key to product_variants: the domain models the link as a bare id
        // with no navigation, and tests and the reaper create stock rows without loading the
        // catalog. Uniqueness above is the invariant that matters for correctness here.
        builder.HasQueryFilter("SoftDelete", stock => stock.DeletedAt == null);
    }
}
