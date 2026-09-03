using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// Reads a failed <see cref="DbUpdateException"/> and says which database constraint refused it.
/// <para>
/// This lives in Infrastructure rather than in the endpoint because it is the one piece of
/// checkout that knows PostgreSQL exists. <see cref="PostgresException.SqlState"/> and the
/// constraint names below are provider facts; if the store ever moved to a different engine the
/// checkout handler would not change, this file would.
/// </para>
/// <para>
/// <strong>Why an exception is the happy path here.</strong> Idempotency is not implemented by
/// selecting first and inserting if nothing came back — that is precisely the race checkout exists
/// to close, since two simultaneous submits both find nothing and both insert. It is implemented
/// by letting both insert and letting the unique index pick the winner. So a unique violation on
/// the idempotency key is not an error to report; it is the answer, and it means "somebody else
/// already created this order, go and read it".
/// </para>
/// </summary>
public static class CheckoutConflicts
{
    /// <summary>SQLSTATE 23505, unique_violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Defined in <c>OrderConfiguration</c>. Repeated here as a literal on purpose: matching a
    /// constraint by name is a runtime contract with the database, and pointing this at a
    /// <c>const</c> in the mapping would let a rename silently turn a recognised replay into an
    /// unhandled 500 with nothing failing to compile.
    /// </summary>
    private const string IdempotencyIndex = "ux_orders_demo_session_id_idempotency_key";

    /// <summary>Defined in <c>OrderConfiguration</c>. See the note on <see cref="IdempotencyIndex"/>.</summary>
    private const string OrderNumberIndex = "ux_orders_order_number";

    /// <summary>
    /// True when the insert lost the race for <c>(demo_session_id, idempotency_key)</c> — a
    /// double-submitted checkout. The caller's response is to roll back and return the order that
    /// already exists, with a 200 rather than an error.
    /// </summary>
    public static bool IsIdempotencyReplay(DbUpdateException exception) =>
        Violated(exception, IdempotencyIndex);

    /// <summary>
    /// True when two orders were minted with the same human-facing number.
    /// <para>
    /// This must never happen: <see cref="OrderNumbers"/> derives the number from a sequence
    /// through a bijection, so distinct sequence values cannot collide. Recognising it anyway
    /// gives the failure a name — the realistic cause is a sequence that was reset or restored out
    /// of step with the table, which is an operational fault a generic 500 would hide.
    /// </para>
    /// </summary>
    public static bool IsOrderNumberCollision(DbUpdateException exception) =>
        Violated(exception, OrderNumberIndex);

    private static bool Violated(DbUpdateException exception, string constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is PostgresException { SqlState: UniqueViolation } postgres
               && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
    }
}
