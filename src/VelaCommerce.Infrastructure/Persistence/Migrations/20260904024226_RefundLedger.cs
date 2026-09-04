using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VelaCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefundLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payment_reference",
                table: "orders",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    gateway_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    restocked_units = table.Column<int>(type: "integer", nullable: false),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_refunds_restocked_units_non_negative", "restocked_units >= 0");
                    table.ForeignKey(
                        name: "fk_refunds_orders",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_order_id",
                table: "refunds",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_refunds_order_id_idempotency_key",
                table: "refunds",
                columns: new[] { "order_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropColumn(
                name: "payment_reference",
                table: "orders");
        }
    }
}
