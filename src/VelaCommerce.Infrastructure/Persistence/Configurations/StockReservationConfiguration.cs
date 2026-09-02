using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VelaCommerce.Domain.Inventory;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the expiring claim on stock that a checkout holds until payment settles.
/// <para>
/// The reaper sweeps this table constantly, so it is indexed for exactly that query — held rows
/// whose expiry has passed — rather than for ad-hoc reporting.
/// </para>
/// </summary>
internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.ToTable(
            "stock_reservations",
            table => table.HasCheckConstraint("ck_stock_reservations_quantity_positive", "quantity > 0"));

        builder.HasKey(reservation => reservation.Id).HasName("pk_stock_reservations");

        builder.Property(reservation => reservation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(reservation => reservation.VariantId)
            .HasColumnName("variant_id");

        builder.Property(reservation => reservation.OrderId)
            .HasColumnName("order_id");

        builder.Property(reservation => reservation.Quantity)
            .HasColumnName("quantity");

        builder.Property(reservation => reservation.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        // Stored as the declared integer rather than a PostgreSQL enum type: the domain fixes the
        // numbers precisely so persisted rows survive a reordering of the C# enum, and an int
        // needs no schema change when a state is added.
        builder.Property(reservation => reservation.Status)
            .HasColumnName("status")
            .HasConversion<int>();

        builder.Property(reservation => reservation.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        // The reaper's query: Status == Held && ExpiresAt <= now.
        builder.HasIndex(reservation => new { reservation.Status, reservation.ExpiresAt })
            .HasDatabaseName("ix_stock_reservations_status_expires_at");

        // Confirming or releasing every reservation for an order is the other access path.
        builder.HasIndex(reservation => reservation.OrderId)
            .HasDatabaseName("ix_stock_reservations_order_id");

        builder.HasIndex(reservation => reservation.VariantId)
            .HasDatabaseName("ix_stock_reservations_variant_id");

        builder.HasQueryFilter("SoftDelete", reservation => reservation.DeletedAt == null);
    }
}
