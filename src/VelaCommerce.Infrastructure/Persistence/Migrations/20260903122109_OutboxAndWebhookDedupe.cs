using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VelaCommerce.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The two tables that make an at-least-once webhook safe in both directions.
    /// <para>
    /// <c>outbox_messages</c> is the sending half: a notification is written in the same
    /// transaction as the order state that promised it, so a state change without its side effect
    /// — or a side effect without its state change — is not a bug to be avoided but a shape the
    /// schema cannot express. <c>processed_webhook_events</c> is the receiving half: the gateway's
    /// event id as the primary key, inserted in the same transaction as the order transition it
    /// authorizes, so a duplicate delivery loses on the key and takes its transition down with it.
    /// </para>
    /// <para>
    /// <strong>Both in one migration, on purpose.</strong> They are written by different code —
    /// the checkout enqueues, the webhook receiver dedupes — and a migration each would mean two
    /// authors editing one model snapshot in the same phase, which is a conflict with no useful
    /// resolution. One migration also states the honest relationship: neither table is worth
    /// having without the other, because at-least-once delivery is exactly what the outbox
    /// guarantees and exactly what the dedupe key is there to survive.
    /// </para>
    /// <para>
    /// <c>ix_outbox_messages_status_deliver_after</c> exists for one query and is ordered for it:
    /// the dispatcher's <c>WHERE status = 0 AND deliver_after &lt;= now ORDER BY deliver_after, id
    /// LIMIT 1 FOR UPDATE SKIP LOCKED</c>. Leading with the status leaves the remainder of the
    /// index already in the order the claim asks for, so the scan stops at the first row it can
    /// lock without sorting anything.
    /// </para>
    /// </summary>
    public partial class OutboxAndWebhookDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    signature_header = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    deliver_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts_non_negative", "attempts >= 0");
                });

            migrationBuilder.CreateTable(
                name: "processed_webhook_events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    order_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_webhook_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_deliver_after",
                table: "outbox_messages",
                columns: new[] { "status", "deliver_after", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_processed_webhook_events_received_at",
                table: "processed_webhook_events",
                column: "received_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "processed_webhook_events");
        }
    }
}
