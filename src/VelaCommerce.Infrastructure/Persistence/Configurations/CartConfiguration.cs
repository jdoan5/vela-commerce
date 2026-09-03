using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VelaCommerce.Domain.Carts;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the cart aggregate. Lines are cascade-deleted with their cart because a line has no
/// meaning outside one, and because clearing a cart should not leave orphans behind for the
/// nightly demo reset to find.
/// </summary>
internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        // The all-zero GUID is the shape a "no session" sentinel takes when someone reaches for
        // one, and a row carrying it would belong to whichever visitor the code next decided to
        // compare against Guid.Empty. Refusing it in the database means the tenancy filter never
        // has to defend against a row that plausibly belongs to everybody.
        builder.ToTable("carts", table => table.HasCheckConstraint(
            "ck_carts_demo_session_id_present",
            "demo_session_id <> '00000000-0000-0000-0000-000000000000'"));

        builder.HasKey(cart => cart.Id).HasName("pk_carts");

        builder.Property(cart => cart.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(cart => cart.DemoSessionId)
            .HasColumnName("demo_session_id");

        builder.Property(cart => cart.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(cart => cart.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        // Sums over the lines; the domain owns the arithmetic and the currency check.
        builder.Ignore(cart => cart.Subtotal);
        builder.Ignore(cart => cart.TotalQuantity);
        builder.Ignore(cart => cart.IsEmpty);

        builder.HasMany(cart => cart.Lines)
            .WithOne()
            .HasForeignKey(line => line.CartId)
            .HasConstraintName("fk_cart_lines_carts")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(nameof(Cart.Lines))
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Not unique: a session may hold a historical cart alongside its live one, and forcing
        // uniqueness here would turn a benign duplicate into a failed page load on the demo.
        builder.HasIndex(cart => cart.DemoSessionId)
            .HasDatabaseName("ix_carts_demo_session_id");

        // The cart's second filter, "DemoTenancy", is added in VelaCommerceDbContext.OnModelCreating
        // rather than here: its predicate has to read an instance member of the context so that EF
        // parameterises the session id per request instead of baking one visitor's id into the
        // cached model, and a configuration found by assembly scan has no context to read.
        builder.HasQueryFilter("SoftDelete", cart => cart.DeletedAt == null);
    }
}

/// <summary>
/// Maps a cart line. SKU and display name are copied rather than joined so the cart still renders
/// if the catalog moves underneath it; the price is revalidated at checkout.
/// </summary>
internal sealed class CartLineConfiguration : IEntityTypeConfiguration<CartLine>
{
    public void Configure(EntityTypeBuilder<CartLine> builder)
    {
        builder.ToTable(
            "cart_lines",
            table => table.HasCheckConstraint("ck_cart_lines_quantity_positive", "quantity > 0"));

        builder.HasKey(line => line.Id).HasName("pk_cart_lines");

        builder.Property(line => line.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(line => line.CartId)
            .HasColumnName("cart_id");

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

        // unit_price * quantity, computed in the domain.
        builder.Ignore(line => line.LineTotal);

        // One line per variant is a domain rule enforced by Cart.AddItem merging duplicates. It is
        // left out of the schema so that a merge bug shows up as two visible lines a tester can
        // report, rather than as a write that fails somewhere deep in checkout.
        builder.HasIndex(line => new { line.CartId, line.VariantId })
            .HasDatabaseName("ix_cart_lines_cart_id_variant_id");

        builder.HasQueryFilter("SoftDelete", line => line.DeletedAt == null);
    }
}
