using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using VelaCommerce.Domain.Messaging;

namespace VelaCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the promise half of the outbox: rows written in a business transaction and delivered by
/// somebody else afterwards.
/// <para>
/// The dispatcher is the only reader, and it always asks the same question — "the oldest due,
/// undelivered message" — so the table is indexed for that one query and nothing else.
/// </para>
/// <para>
/// <b>No query filter here, and that is deliberate.</b> Every other mapped type in this model
/// carries <c>SoftDelete</c>, and carts and orders carry <c>DemoTenancy</c> on top of it. Neither
/// belongs on this table. A settlement notification is owned by a payment rather than by a browser
/// session, so a tenancy filter would make the dispatcher — which has no visitor and against which
/// the filter fails closed — see an empty table forever. And a filter of any kind would compose
/// itself onto the dispatcher's <c>FOR UPDATE SKIP LOCKED</c> claim, wrapping the one statement in
/// this system that has to reach PostgreSQL exactly as written. The absence is the reason the
/// dispatcher never needs <c>IgnoreQueryFilters</c>, which is worth knowing before somebody adds
/// one here for consistency.
/// </para>
/// </summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable(
            "outbox_messages",
            table => table.HasCheckConstraint("ck_outbox_messages_attempts_non_negative", "attempts >= 0"));

        builder.HasKey(message => message.Id).HasName("pk_outbox_messages");

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(message => message.MessageType)
            .HasColumnName("message_type")
            .HasMaxLength(100)
            .IsRequired();

        // text, not varchar(n). A payload is whatever the sender signed, and a length limit here
        // would be a cap on what this system can promise to deliver — enforced, unhelpfully, at
        // insert time inside a checkout. PostgreSQL stores both identically anyway.
        builder.Property(message => message.Payload)
            .HasColumnName("payload")
            .IsRequired();

        // Comfortably over the 85 characters "t=<10>,v1=<64 hex>" needs, with room for a second
        // scheme version to be sent alongside the first when v2 arrives.
        builder.Property(message => message.SignatureHeader)
            .HasColumnName("signature_header")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(message => message.DeliverAfter)
            .HasColumnName("deliver_after")
            .HasColumnType("timestamptz");

        builder.Property(message => message.Attempts)
            .HasColumnName("attempts");

        // Stored as the declared integer rather than a PostgreSQL enum type, for the same reason
        // the other status columns are: the enum fixes its numbers, so persisted rows survive a
        // reordering of the C# type, and adding a state needs no schema change.
        builder.Property(message => message.Status)
            .HasColumnName("status")
            .HasConversion<int>();

        builder.Property(message => message.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(OutboxMessage.MaxErrorLength);

        builder.Property(message => message.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(message => message.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");

        builder.Property(message => message.DeliveredAt)
            .HasColumnName("delivered_at")
            .HasColumnType("timestamptz");

        // The dispatcher's claim, exactly: WHERE status = 0 AND deliver_after <= now ORDER BY
        // deliver_after, id LIMIT 1. With the status leading, the rest of the index is already in
        // the order the claim asks for, so the query is an index scan that stops at the first row
        // it can lock and never sorts.
        //
        // Not a partial index (WHERE status = 0), which would be smaller and is the obvious idea.
        // The status arrives as a bound parameter, and PostgreSQL can only use a partial index
        // when it can prove the predicate holds — which it cannot do for `status = $1` once the
        // planner settles on a generic plan. An index that works on the first five executions and
        // then silently stops being used is worse than a slightly larger one that always does.
        builder.HasIndex(message => new { message.Status, message.DeliverAfter, message.Id })
            .HasDatabaseName("ix_outbox_messages_status_deliver_after");
    }
}
