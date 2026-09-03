using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using VelaCommerce.Infrastructure.Messaging;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the webhook dedupe ledger.
/// <para>
/// The entire mechanism is the primary key. The receiver inserts the gateway's event id and
/// applies the order transition in one transaction, so a duplicate delivery fails on this key and
/// takes its transition down with it — no <c>SELECT</c> first, and therefore no window between
/// asking "have I seen this?" and acting on the answer. That window is where every
/// almost-idempotent webhook handler goes wrong, and it cannot be closed by checking harder; it is
/// closed by making the check and the effect the same commit.
/// </para>
/// <para>
/// A natural key rather than a surrogate with a unique index beside it. The two enforce the same
/// thing, but the natural key says what the row is <em>for</em>: this table has no identity of its
/// own to hand out, and a second key would invite a second way to write a row that already exists.
/// </para>
/// <para>
/// <b>Mapped here rather than by the receiver.</b> The receiver writes these rows but adds no
/// migration: this table ships in the same migration as <c>outbox_messages</c> so one phase leaves
/// one migration, and two agents adding one each cannot collide on the model snapshot.
/// </para>
/// <para>
/// No soft delete and no tenancy filter, for the same reason as the outbox: a delivery record
/// belongs to a payment, not to a visitor, and a filtered read here would be a dedupe check that
/// silently sees nothing — which is to say, no dedupe at all.
/// </para>
/// </summary>
internal sealed class ProcessedWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedWebhookEvent> builder)
    {
        builder.ToTable("processed_webhook_events");

        builder.HasKey(processed => processed.EventId).HasName("pk_processed_webhook_events");

        builder.Property(processed => processed.EventId)
            .HasColumnName("event_id")
            .HasMaxLength(ProcessedWebhookEvent.MaxEventIdLength)
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(processed => processed.ReceivedAt)
            .HasColumnName("received_at")
            .HasColumnType("timestamptz");

        // Both nullable: a receiver that has verified a signature but not yet parsed the body
        // still has an event id, and recording the id is the part that must not be optional.
        builder.Property(processed => processed.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(64);

        builder.Property(processed => processed.OrderReference)
            .HasColumnName("order_reference")
            .HasMaxLength(32);

        // For pruning. This table only grows, and the row that matters is the key rather than its
        // history, so an eventual "delete everything older than n days" job needs a cheap way to
        // find them. One index now is cheaper than an unindexed sweep over the whole table later.
        builder.HasIndex(processed => processed.ReceivedAt)
            .HasDatabaseName("ix_processed_webhook_events_received_at");
    }
}
