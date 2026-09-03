using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VelaCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DemoSessionTenancyGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_demo_session_id_present",
                table: "orders",
                sql: "demo_session_id <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_carts_demo_session_id_present",
                table: "carts",
                sql: "demo_session_id <> '00000000-0000-0000-0000-000000000000'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_demo_session_id_present",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_carts_demo_session_id_present",
                table: "carts");
        }
    }
}
