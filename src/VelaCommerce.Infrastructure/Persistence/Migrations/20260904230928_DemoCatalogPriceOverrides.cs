using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VelaCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DemoCatalogPriceOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "demo_catalog_price_overrides",
                columns: table => new
                {
                    demo_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_amount = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demo_catalog_price_overrides", x => new { x.demo_session_id, x.variant_id });
                    table.CheckConstraint("ck_demo_catalog_price_overrides_demo_session_id_present", "demo_session_id <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("ck_demo_catalog_price_overrides_price_non_negative", "price_amount >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "demo_catalog_price_overrides");
        }
    }
}
