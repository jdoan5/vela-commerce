using VelaCommerce.Domain.Messaging;
using VelaCommerce.Infrastructure.Payments;

namespace VelaCommerce.Infrastructure.Messaging;

/// <summary>
/// Turns the payment simulator's delivery plan into outbox rows.
/// <para>
/// A tiny translation, kept in one named place for two reasons. It is the seam between the two
/// halves of this phase — the gateway decides <em>what</em> will be delivered and <em>when</em>,
/// the outbox owns <em>that</em> it will be — and it is the one conversion where getting the
/// payload wrong is invisible until a receiver reports a signature failure, so it is worth being
/// able to point at the four lines that carry it.
/// </para>
/// <para>
/// It takes no <c>DbContext</c> and adds nothing to one. The caller enqueues what this returns
/// inside its own transaction, which is what keeps the decision about <em>when</em> the promise is
/// committed with the caller who knows what it is being committed alongside.
/// </para>
/// </summary>
public static class OutboxNotifications
{
    /// <summary>
    /// Maps signed notifications to messages ready to be added to the outbox.
    /// </summary>
    /// <param name="notifications">
    /// The plan from <see cref="IPaymentSimulator.Simulate"/>. Usually empty — only the deferred
    /// scenarios produce one, and a synchronous capture or a decline produces none, because
    /// nothing is going to settle later.
    /// </param>
    /// <param name="now">
    /// The instant the payment was authorized. Each notification's
    /// <see cref="SignedPaymentNotification.DeliverAfter"/> is a <em>relative</em> delay — the
    /// simulator deliberately refuses to name an instant on a clock it does not own — so this is
    /// where it becomes an absolute one. Passing the checkout's single <c>now</c> keeps every row
    /// that checkout wrote agreeing about when it happened.
    /// </param>
    public static IReadOnlyList<OutboxMessage> ToMessages(
        IReadOnlyList<SignedPaymentNotification> notifications,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        if (notifications.Count == 0)
            return [];

        var messages = new List<OutboxMessage>(notifications.Count);

        foreach (var notification in notifications)
        {
            messages.Add(new OutboxMessage(
                notification.Event.EventType,

                // notification.Payload, not a re-serialization of notification.Event. These are the
                // bytes the signature was computed over; the event object beside them exists for
                // logs and assertions and is not the message.
                notification.Payload,
                notification.Signature,
                now + notification.DeliverAfter,
                now));
        }

        return messages;
    }
}
