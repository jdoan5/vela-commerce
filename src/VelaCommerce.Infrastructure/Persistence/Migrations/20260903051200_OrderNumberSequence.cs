using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VelaCommerce.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the sequence that human-facing order numbers are minted from.
    /// <para>
    /// Checkout needs a source of values that is unique across concurrent requests, and a sequence
    /// is the only thing PostgreSQL offers that promises it without coordination: <c>nextval</c>
    /// never hands the same value to two callers, and never re-hands one after a rollback.
    /// <c>OrderNumbers.Format</c> then maps a value through a bijection into seven Crockford Base32
    /// characters, so distinct sequence values cannot produce the same reference and there is no
    /// collision to retry around. <c>ux_orders_order_number</c> remains the backstop.
    /// </para>
    /// <para>
    /// <strong>Raw SQL rather than <c>migrationBuilder.CreateSequence</c>, deliberately.</strong>
    /// A sequence created through the builder would have to exist in the EF model as
    /// <c>HasSequence</c> for the model snapshot to know about it — and it does not belong there,
    /// because no entity property is generated from it. Created outside the model, the sequence is
    /// invisible to the model differ, so the next <c>migrations add</c> will neither drop it nor
    /// try to recreate it.
    /// </para>
    /// <para>
    /// The name is repeated as a literal here rather than read from
    /// <c>VelaCommerce.Infrastructure.Checkout.OrderNumbers.SequenceName</c>. Migrations are
    /// historical records: this one has to keep producing the same DDL after that constant is
    /// renamed, moved or deleted, which a compile-time reference would quietly prevent.
    /// </para>
    /// </summary>
    public partial class OrderNumberSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AS bigint: the encoding is injective over [1, 2^35), so a 32-bit sequence would be
            // an arbitrary second ceiling well below the one the format actually has.
            // NO CYCLE: wrapping would re-issue numbers that already belong to orders, which is the
            // one failure this whole mechanism exists to prevent — better to fail loudly at 2^63.
            // CACHE 1: gaps are acceptable (they are what stops the number being read as a sales
            // count) but per-connection caching would also reorder issuance across pooled
            // connections for no benefit at this volume.
            migrationBuilder.Sql("""
                CREATE SEQUENCE IF NOT EXISTS order_number_seq
                    AS bigint
                    START WITH 1
                    INCREMENT BY 1
                    MINVALUE 1
                    NO CYCLE
                    CACHE 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the sequence loses the high-water mark, so re-applying this migration would
            // start minting order numbers that existing rows already hold. Down is here because a
            // migration without one cannot be reverted at all, not because reverting is safe once
            // orders exist.
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS order_number_seq;");
        }
    }
}
