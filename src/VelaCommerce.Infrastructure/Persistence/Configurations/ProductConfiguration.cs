using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VelaCommerce.Domain.Catalog;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the catalog aggregate root. Variants are written through this navigation only, which is
/// what keeps <see cref="Product.AddVariant"/> the single place a SKU can be introduced.
/// </summary>
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>
    /// Attributes are serialised to a string here instead of being handed to the provider as a
    /// dictionary, for two reasons. An unconverted <c>Dictionary&lt;string, string&gt;</c> maps to
    /// <c>hstore</c>, which needs an extension and cannot nest; and POCO-to-jsonb mapping asks the
    /// host to enable dynamic JSON on the data source. Converting here means the column is jsonb
    /// no matter how the API happens to build its connection.
    /// </summary>
    private static readonly ValueConverter<Dictionary<string, string>, string> AttributesConverter = new(
        attributes => JsonSerializer.Serialize(attributes, JsonSerializerOptions.Default),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Default)
                ?? new Dictionary<string, string>());

    /// <summary>
    /// A converted mutable collection needs an explicit comparer, otherwise the change tracker
    /// compares references and never notices an edited facet. The hash folds pairs with XOR so it
    /// does not depend on dictionary enumeration order.
    /// </summary>
    private static readonly ValueComparer<Dictionary<string, string>> AttributesComparer = new(
        (left, right) => left == null
            ? right == null
            : right != null
              && left.Count == right.Count
              && left.All(pair => right.ContainsKey(pair.Key) && right[pair.Key] == pair.Value),
        attributes => attributes.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        attributes => attributes.ToDictionary(pair => pair.Key, pair => pair.Value));

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");

        builder.HasKey(product => product.Id).HasName("pk_products");

        // The domain hands out UUIDv7 in the Entity base, so the database must not try to generate
        // a key of its own: an EF-side sequence or a DEFAULT would fight the value already set.
        builder.Property(product => product.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(product => product.Slug)
            .HasColumnName("slug")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(product => product.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        // Left as text: marketing copy has no natural ceiling and PostgreSQL stores it out of line
        // once it is large, so a varchar cap would buy nothing.
        builder.Property(product => product.Description)
            .HasColumnName("description")
            .IsRequired();

        builder.Property(product => product.Category)
            .HasColumnName("category")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(product => product.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasConversion(AttributesConverter, AttributesComparer)
            .IsRequired();

        builder.Property(product => product.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        // Derived in the domain from the variants; nothing to store.
        builder.Ignore(product => product.FromPrice);

        builder.HasMany(product => product.Variants)
            .WithOne(variant => variant.Product!)
            .HasForeignKey(variant => variant.ProductId)
            .HasConstraintName("fk_product_variants_products")
            .OnDelete(DeleteBehavior.Cascade);

        // The list is private; EF must go through _variants rather than the read-only property so
        // that materialising a product does not need a public mutator the domain deliberately omits.
        builder.Navigation(nameof(Product.Variants))
            .HasField("_variants")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Unfiltered on purpose: a soft-deleted product keeps its slug reserved, so an old link
        // can never start resolving to a different product after a restore.
        builder.HasIndex(product => product.Slug)
            .IsUnique()
            .HasDatabaseName("ux_products_slug");

        builder.HasIndex(product => product.Category)
            .HasDatabaseName("ix_products_category");

        // Trigram GIN indexes, one per column the search reads. GIN and not GiST: GIN is larger
        // and slower to build and answers `%term%` faster, which is the only shape this query has.
        //
        // These serve `ILIKE '%term%'` specifically. A B-tree cannot - a leading wildcard means
        // there is no prefix to seek on, so the planner has no choice but to read every row. That
        // is why CatalogEndpoints uses ILIKE against the untouched column rather than
        // `lower(name) LIKE`: wrapping the column in a function makes it unindexable by anything
        // except a matching expression index.
        //
        // A term shorter than three characters produces no trigrams and the planner falls back to
        // a sequential scan whatever is indexed here. That is a property of trigrams, not a bug,
        // and it is asserted rather than assumed - see CatalogSearchIndexTests.
        builder.HasIndex(product => product.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_products_name_trgm");

        builder.HasIndex(product => product.Description)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_products_description_trgm");

        builder.HasQueryFilter("SoftDelete", product => product.DeletedAt == null);
    }
}

/// <summary>
/// Maps the buyable SKU. Priced in minor units so no rounding decision is ever delegated to the
/// database or to a floating-point type.
/// </summary>
internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable(
            "product_variants",
            table => table.HasCheckConstraint("ck_product_variants_price_non_negative", "price_amount >= 0"));

        builder.HasKey(variant => variant.Id).HasName("pk_product_variants");

        builder.Property(variant => variant.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(variant => variant.ProductId)
            .HasColumnName("product_id");

        builder.Property(variant => variant.Sku)
            .HasColumnName("sku")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(variant => variant.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(variant => variant.ImageUrl)
            .HasColumnName("image_url")
            .HasMaxLength(512);

        // Money is a value, not an entity: a complex property keeps it in the variant's own row as
        // two columns instead of inventing a table and a join for something with no identity.
        builder.ComplexProperty(variant => variant.Price, price =>
        {
            price.Property(money => money.Amount)
                .HasColumnName("price_amount");

            price.Property(money => money.Currency)
                .HasColumnName("price_currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        builder.Property(variant => variant.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        // SKUs are the shared vocabulary between the catalog, the warehouse and the order history,
        // so uniqueness is global rather than per product.
        builder.HasIndex(variant => variant.Sku)
            .IsUnique()
            .HasDatabaseName("ux_product_variants_sku");

        builder.HasIndex(variant => variant.ProductId)
            .HasDatabaseName("ix_product_variants_product_id");

        builder.HasQueryFilter("SoftDelete", variant => variant.DeletedAt == null);
    }
}
