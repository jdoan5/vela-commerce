using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VelaCommerce.Domain.Common;
using VelaCommerce.Domain.Orders;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the checkout aggregate, including the two invariants the plan refuses to trust to C#
/// alone: one order per double-submitted checkout, and refunds that never exceed the capture.
/// Both are constraints below, so a second process, a bad migration or a hand-written UPDATE
/// hits the same wall the domain does.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>
    /// The address is frozen at checkout and never queried across orders, so it is one jsonb
    /// column rather than a table and a join. Serialising in the converter — instead of letting
    /// the provider serialise a POCO — keeps the mapping independent of whether the host enabled
    /// dynamic JSON on its data source.
    /// </summary>
    private static readonly ValueConverter<ShippingAddress, string> ShippingAddressConverter = new(
        address => JsonSerializer.Serialize(address, JsonSerializerOptions.Default),
        json => JsonSerializer.Deserialize<ShippingAddress>(json, JsonSerializerOptions.Default)!);

    /// <summary>
    /// A record compares structurally and never mutates in place, so snapshotting can hand back
    /// the same instance and equality can defer to the generated operator.
    /// </summary>
    private static readonly ValueComparer<ShippingAddress> ShippingAddressComparer = new(
        (left, right) => left == right,
        address => address.GetHashCode(),
        address => address);

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", table =>
        {
            table.HasCheckConstraint("ck_orders_captured_non_negative", "captured_amount >= 0");
            table.HasCheckConstraint("ck_orders_refunded_non_negative", "refunded_amount >= 0");
            table.HasCheckConstraint("ck_orders_refund_within_capture", "refunded_amount <= captured_amount");

            // Same reasoning as the carts table: the all-zero GUID is what a "no session" sentinel
            // looks like, and an order carrying it would read as belonging to whoever happened to
            // be compared against Guid.Empty. Unrepresentable is better than filtered.
            table.HasCheckConstraint(
                "ck_orders_demo_session_id_present",
                "demo_session_id <> '00000000-0000-0000-0000-000000000000'");
        });

        builder.HasKey(order => order.Id).HasName("pk_orders");

        builder.Property(order => order.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(order => order.DemoSessionId)
            .HasColumnName("demo_session_id");

        builder.Property(order => order.OrderNumber)
            .HasColumnName("order_number")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(order => order.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(order => order.Status)
            .HasColumnName("status")
            .HasConversion<int>();

        builder.Property(order => order.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(order => order.ShippingAddress)
            .HasColumnName("shipping_address")
            .HasColumnType("jsonb")
            .HasConversion(ShippingAddressConverter, ShippingAddressComparer)
            .IsRequired();

        builder.Property(order => order.PlacedAt)
            .HasColumnName("placed_at")
            .HasColumnType("timestamptz");

        builder.Property(order => order.PaidAt)
            .HasColumnName("paid_at")
            .HasColumnType("timestamptz");

        builder.Property(order => order.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        // Each Money keeps its own currency column. The redundancy against orders.currency is the
        // price of storing a whole value rather than a bare number, and it means a row read in
        // isolation still says what it is worth.
        builder.ComplexProperty(order => order.Shipping, shipping =>
        {
            shipping.Property(money => money.Amount).HasColumnName("shipping_amount");
            shipping.Property(money => money.Currency).HasColumnName("shipping_currency").HasMaxLength(3).IsRequired();
        });

        builder.ComplexProperty(order => order.Tax, tax =>
        {
            tax.Property(money => money.Amount).HasColumnName("tax_amount");
            tax.Property(money => money.Currency).HasColumnName("tax_currency").HasMaxLength(3).IsRequired();
        });

        builder.ComplexProperty(order => order.Captured, captured =>
        {
            captured.Property(money => money.Amount).HasColumnName("captured_amount");
            captured.Property(money => money.Currency).HasColumnName("captured_currency").HasMaxLength(3).IsRequired();
        });

        builder.ComplexProperty(order => order.Refunded, refunded =>
        {
            refunded.Property(money => money.Amount).HasColumnName("refunded_amount");
            refunded.Property(money => money.Currency).HasColumnName("refunded_currency").HasMaxLength(3).IsRequired();
        });

        // Totals are derived from the lines and the two adjustment columns. Storing them would let
        // a stored total disagree with the lines that justify it.
        builder.Ignore(order => order.Subtotal);
        builder.Ignore(order => order.Total);
        builder.Ignore(order => order.RefundableRemaining);

        builder.HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .HasConstraintName("fk_order_lines_orders")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(nameof(Order.Lines))
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // This index is the whole double-submit defence: the second insert with the same key loses
        // on a unique violation, and the API turns that into a replay of the first order. Scoped by
        // session so two demo visitors can reuse an obvious key like "1" without colliding, and
        // deliberately unfiltered so a soft-deleted order still blocks its key.
        builder.HasIndex(order => new { order.DemoSessionId, order.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_orders_demo_session_id_idempotency_key");

        // The confirmation page and any support conversation key off this number, so a duplicate
        // would be a genuine bug rather than a nuisance. Unique here means the generator fails
        // loudly at insert instead of quietly handing two orders the same human-facing reference.
        builder.HasIndex(order => order.OrderNumber)
            .IsUnique()
            .HasDatabaseName("ux_orders_order_number");

        // The demo's order history: newest first for one session.
        builder.HasIndex(order => new { order.DemoSessionId, order.PlacedAt })
            .HasDatabaseName("ix_orders_demo_session_id_placed_at");

        // The order's second filter, "DemoTenancy", is added in VelaCommerceDbContext.OnModelCreating
        // rather than here: its predicate has to read an instance member of the context so that EF
        // parameterises the session id per request instead of baking one visitor's id into the
        // cached model, and a configuration found by assembly scan has no context to read.
        //
        // Note what that filter does NOT cover: the unique index above is deliberately unfiltered,
        // so a replayed idempotency key still collides even for a session the reader cannot see.
        builder.HasQueryFilter("SoftDelete", order => order.DeletedAt == null);
    }
}

/// <summary>
/// Maps an order line. SKU, name and price are the values captured at checkout, not a live join,
/// so the order still reads correctly after the catalog is renamed, repriced or deleted.
/// </summary>
internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable(
            "order_lines",
            table => table.HasCheckConstraint("ck_order_lines_quantity_positive", "quantity > 0"));

        builder.HasKey(line => line.Id).HasName("pk_order_lines");

        builder.Property(line => line.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(line => line.OrderId)
            .HasColumnName("order_id");

        builder.Property(line => line.VariantId)
            .HasColumnName("variant_id");

        builder.Property(line => line.Sku)
            .HasColumnName("sku")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(line => line.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.ComplexProperty(line => line.UnitPrice, unitPrice =>
        {
            unitPrice.Property(money => money.Amount)
                .HasColumnName("unit_price_amount");

            unitPrice.Property(money => money.Currency)
                .HasColumnName("unit_price_currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        builder.Property(line => line.Quantity)
            .HasColumnName("quantity");

        builder.Property(line => line.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        builder.Ignore(line => line.LineTotal);

        builder.HasIndex(line => line.OrderId)
            .HasDatabaseName("ix_order_lines_order_id");

        builder.HasQueryFilter("SoftDelete", line => line.DeletedAt == null);
    }
}
