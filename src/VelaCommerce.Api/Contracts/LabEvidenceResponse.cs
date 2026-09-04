namespace VelaCommerce.Api.Contracts;

/// <summary>
/// What the database says happened, read after the run and outside every visitor's session.
/// <para>
/// The transcript above it is a record of what the shop <em>answered</em>. This is the record of
/// what it <em>did</em>, and the two are not the same claim: a shop that replied 201 to five
/// shoppers and reserved eight units would produce a perfect transcript. Every assertion in the
/// verdict that matters is made against these rows rather than against a status code, for the
/// reason the settlement suite gives - a receiver that answered 200 twice and charged twice, and
/// one that answered 200 twice and charged nothing, are indistinguishable from the outside.
/// </para>
/// </summary>
/// <param name="Orders">Every order the run created, as the table holds it.</param>
/// <param name="Ledger">The stock ledger for each fixture variant, before and after.</param>
/// <param name="Reservations">Every reservation raised, and the status the reaper reads.</param>
/// <param name="Settlements">The gateway notifications the run produced, and how often each was applied.</param>
/// <param name="BlastRadius">What this run touched that it did not create, and what it left behind.</param>
public sealed record LabEvidenceResponse(
    IReadOnlyList<LabOrderResponse> Orders,
    IReadOnlyList<LabLedgerResponse> Ledger,
    IReadOnlyList<LabReservationResponse> Reservations,
    IReadOnlyList<LabSettlementResponse> Settlements,
    LabBlastRadiusResponse BlastRadius);

/// <summary>One order row.</summary>
/// <param name="OrderNumber">The number the shopper was given.</param>
/// <param name="Status">Where the state machine left it.</param>
/// <param name="Total">What was owed.</param>
/// <param name="Captured">What was actually taken. The number a double-charge would move.</param>
/// <param name="Refunded">What has gone back. The number a double-refund would move.</param>
/// <param name="PlacedAt">When it was created.</param>
/// <param name="PaidAt">When it was paid, if it was.</param>
/// <param name="Quantity">Units across all lines.</param>
/// <param name="Visitor">
/// A short fingerprint of the owning session, never the session id itself. Enough to see that five
/// orders belong to five different shoppers - which is the assertion - without publishing an
/// identifier that names a visitor.
/// </param>
/// <param name="RowVersion">
/// PostgreSQL's <c>xmin</c>: the id of the transaction that last wrote this row. Present only where
/// a scenario needs it, because it is the one piece of evidence a duplicate delivery cannot satisfy
/// by writing the same values a second time.
/// </param>
public sealed record LabOrderResponse(
    string OrderNumber,
    string Status,
    MoneyDto Total,
    MoneyDto Captured,
    MoneyDto Refunded,
    DateTimeOffset PlacedAt,
    DateTimeOffset? PaidAt,
    int Quantity,
    string Visitor,
    string? RowVersion);

/// <summary>
/// The two numbers the whole stock argument is about, at both ends of the run.
/// <para>
/// Shown before and after because a single "after" reading proves nothing on its own: the claim is
/// that the ledger moved by exactly the amount sold, and the only way to see that is to have the
/// figure it started from.
/// </para>
/// </summary>
/// <param name="Sku">The fixture SKU. Private to this run - see the blast-radius note.</param>
/// <param name="DisplayName">What the fixture was called.</param>
/// <param name="OnHandBefore">Physical units before.</param>
/// <param name="ReservedBefore">Units promised before.</param>
/// <param name="AvailableBefore">What a shopper could take before.</param>
/// <param name="OnHandAfter">Physical units after.</param>
/// <param name="ReservedAfter">Units promised after.</param>
/// <param name="AvailableAfter">What a shopper could take after.</param>
public sealed record LabLedgerResponse(
    string Sku,
    string DisplayName,
    int OnHandBefore,
    int ReservedBefore,
    int AvailableBefore,
    int OnHandAfter,
    int ReservedAfter,
    int AvailableAfter);

/// <summary>One stock reservation.</summary>
/// <param name="Sku">Which fixture variant is held.</param>
/// <param name="OrderNumber">The order holding it, or a placeholder if its order is gone.</param>
/// <param name="Quantity">Units held.</param>
/// <param name="Status">Held, Confirmed or Released - what stops the reaper handing them back.</param>
public sealed record LabReservationResponse(
    string Sku,
    string OrderNumber,
    int Quantity,
    string Status);

/// <summary>
/// One settlement notification: the promise checkout wrote, and what the receiver did about it.
/// </summary>
/// <param name="OrderNumber">The order the event refers to.</param>
/// <param name="MessageType">The event type, as the gateway named it.</param>
/// <param name="EventId">The gateway's id for the event. The value the dedupe is keyed on.</param>
/// <param name="Status">Where the outbox row got to: Pending, Delivered or Abandoned.</param>
/// <param name="Attempts">Delivery attempts made by the shop's own dispatcher.</param>
/// <param name="DeliverAfter">The earliest instant it may be sent.</param>
/// <param name="SignatureHeader">
/// The complete <c>X-Vela-Signature</c> value stored beside the payload. Shown in full on purpose:
/// it is a MAC over bytes plus a timestamp, not a secret, and seeing it is how a reader confirms
/// the redelivery carried the gateway's own signature rather than a fresh one.
/// </param>
/// <param name="TimesApplied">
/// How many rows this event id has in <c>processed_webhook_events</c>. The exactly-once claim, as
/// an integer. Anything but one is a broken invariant.
/// </param>
public sealed record LabSettlementResponse(
    string OrderNumber,
    string MessageType,
    string EventId,
    string Status,
    int Attempts,
    DateTimeOffset DeliverAfter,
    string SignatureHeader,
    int TimesApplied);

/// <summary>
/// What a public, unauthenticated run endpoint did to everybody else's shop.
/// <para>
/// The honest answer has to be an audited number rather than a promise, which is what this record
/// is. A fifty-way race against a real product would exhaust it for every other visitor, so the lab
/// never touches the shared catalog: it seeds its own product, its own variant and its own stock
/// for the run, and destroys all of it afterwards. <see cref="SharedCatalogRowsTouched"/> is
/// therefore zero by construction, and <see cref="FixtureRemoved"/> is verified by re-reading the
/// tables after the teardown rather than assumed from the fact that a delete was issued.
/// </para>
/// </summary>
/// <param name="StockStrategy">Which of the three possible strategies this lab uses.</param>
/// <param name="Explanation">Why, and what the alternatives would have cost.</param>
/// <param name="SharedCatalogRowsTouched">
/// Rows belonging to the seeded catalog that this run wrote. Zero, and stated as a number so that
/// a future change which breaks it shows up here rather than in somebody's empty basket.
/// </param>
/// <param name="FixtureRemoved">Whether nothing at all is left, re-read after the deletes.</param>
/// <param name="Removed">What was deleted, by table.</param>
/// <param name="Warning">
/// What survived, if anything did. Null on a clean teardown. A run that cannot clean up after
/// itself must say so loudly rather than leave a reviewer to find the debris.
/// </param>
public sealed record LabBlastRadiusResponse(
    string StockStrategy,
    string Explanation,
    int SharedCatalogRowsTouched,
    bool FixtureRemoved,
    IReadOnlyList<LabRowsRemovedResponse> Removed,
    string? Warning);

/// <summary>Rows deleted from one table by the teardown.</summary>
/// <param name="Table">The table name, as PostgreSQL holds it.</param>
/// <param name="Rows">How many rows went.</param>
public sealed record LabRowsRemovedResponse(string Table, int Rows);
