namespace VelaCommerce.Infrastructure.DemoLab;

/// <summary>
/// Every scenario the Demo Lab offers, and the ids the run route accepts.
/// <para>
/// The list is here rather than inside the endpoint for the same reason
/// <c>PaymentScenarioCatalog</c> is: it is a description of what this system claims about itself,
/// and the storefront, the OpenAPI document and the run handler all need the same copy of it. Two
/// copies would drift, and the first symptom would be a button whose label no longer matches what
/// it does.
/// </para>
/// <para>
/// <b>Every entry names a test file, and that is the point of the whole feature.</b> The repository
/// claims a set of invariants; the suite proves them on every push; but a reviewer with ten minutes
/// is not going to run a test suite, and a README assertion is worth approximately nothing. The lab
/// lets them press a button and watch the same invariant hold against the same code — and then
/// hands them the file that says CI agrees. A scenario that could not name a test would be a claim
/// dressed up as a demonstration, which is worse than no lab at all.
/// </para>
/// </summary>
public static class DemoLabScenarioCatalog
{
    private const string StockRaceTests =
        "tests/VelaCommerce.Integration.Tests/CheckoutStockRaceTests.cs";

    private const string IdempotencyTests =
        "tests/VelaCommerce.Integration.Tests/CheckoutIdempotencyTests.cs";

    private const string SettlementTests =
        "tests/VelaCommerce.Integration.Tests/SettlementExactlyOnceTests.cs";

    private const string PaymentTests =
        "tests/VelaCommerce.Integration.Tests/PaymentSimulatorTests.cs";

    private const string RefundTests =
        "tests/VelaCommerce.Integration.Tests/RefundTests.cs";

    /// <summary>The scenario a page should offer first: the headline claim.</summary>
    public const string Oversell = "oversell";

    /// <summary>Two shoppers, one unit, and a refusal that names the item.</summary>
    public const string LastUnit = "last-unit";

    /// <summary>A cart that cannot be filled buys nothing and gives back what it took.</summary>
    public const string PartialRollback = "partial-rollback";

    /// <summary>A refused card puts the unit back on the shelf.</summary>
    public const string DeclinedPayment = "declined-payment";

    /// <summary>A double-clicked Pay button that makes one order.</summary>
    public const string DoubleSubmit = "double-submit";

    /// <summary>The same settlement delivered twice, paid once.</summary>
    public const string SettlementReplay = "settlement-replay";

    /// <summary>Two copies of one settlement arriving together, applied once.</summary>
    public const string SettlementRace = "settlement-race";

    /// <summary>All six simulator scenarios, side by side.</summary>
    public const string PaymentScenarios = "payment-scenarios";

    /// <summary>Many hands reaching for one balance, and it goes back once.</summary>
    public const string RefundRace = "refund-race";

    /// <summary>
    /// The catalogue, in the order a page should show it: the three stock races first, because
    /// they are the ones people disbelieve; then the two ways a button press can be duplicated;
    /// then the payment table.
    /// </summary>
    public static IReadOnlyList<DemoLabScenarioDescriptor> Descriptors { get; } =
    [
        new(
            Id: Oversell,
            Title: "Fifty shoppers, five units",
            Claim: "Fifty people reach for five units at the same instant. Exactly five are sold, "
                   + "forty-five are told the item ran out, and nobody sees a server error.",
            Invariant: "reserved never exceeds on_hand, however many checkouts overlap.",
            Mechanism: "One guarded statement decides every winner: UPDATE stock_items SET reserved "
                       + "= reserved + q WHERE variant_id = v AND deleted_at IS NULL AND on_hand - "
                       + "reserved >= q. The row count is the answer - 1 won, 0 lost. Nothing loads a "
                       + "StockItem and asks it, because two requests holding two copies of one row "
                       + "both get told yes. ck_stock_items_reserved_within_on_hand is the backstop.",
            ProvenBy: StockRaceTests,
            ProvenByTest: "Fifty_shoppers_racing_for_five_units_sell_exactly_five",
            Participants: 50,
            Units: 5,
            Creates: "50 throwaway visitor sessions, 50 carts, 5 orders, 5 stock reservations - all "
                     + "against one private fixture variant that this run creates and destroys.",
            Fidelity: "Genuine. Fifty real HTTP checkouts, released together on one gate, against "
                      + "real PostgreSQL. Nothing here is replayed or simulated."),

        new(
            Id: LastUnit,
            Title: "Two shoppers, one unit",
            Claim: "Two people reach for the last unit together. One buys it; the other is told "
                   + "which item ran out and how many were left - not \"something went wrong\".",
            Invariant: "Losing a race for stock is a commercial answer (409 naming the SKU), never a "
                       + "500 and never a constraint violation leaking out of the database.",
            Mechanism: "The same guarded UPDATE. The refusal carries a shortfall object - variant, "
                       + "SKU, requested, available - so a storefront can highlight the row instead "
                       + "of apologising in general terms.",
            ProvenBy: StockRaceTests,
            ProvenByTest: "Two_shoppers_racing_for_the_last_unit_are_told_which_one_ran_out",
            Participants: 2,
            Units: 1,
            Creates: "2 throwaway sessions, 2 carts, 1 order, 1 reservation, on a private fixture.",
            Fidelity: "Genuine. Both checkouts are released on one gate, so they are inside the "
                      + "critical section together rather than one after the other."),

        new(
            Id: PartialRollback,
            Title: "A cart that cannot be filled buys nothing",
            Claim: "A two-line cart whose second line has run out buys neither line - and the units "
                   + "already taken for the first line go back on the shelf immediately.",
            Invariant: "A checkout is all-or-nothing. Stock taken for a line that never becomes an "
                       + "order is released before the request returns.",
            Mechanism: "The reservations are uncommitted increments inside one transaction, so "
                       + "rolling back IS the release - not a compensating step somebody could "
                       + "forget. Lines are reserved in variant-id order so two shoppers buying the "
                       + "same two items in opposite cart order cannot deadlock.",
            ProvenBy: StockRaceTests,
            ProvenByTest: "A_checkout_that_cannot_fill_every_line_gives_back_what_it_had_already_taken",
            Participants: 4,
            Units: 20,
            Creates: "4 throwaway sessions, 4 carts, 3 orders and their reservations, across two "
                     + "private fixture variants.",
            Fidelity: "Genuine. The shortage is created by another shopper genuinely buying the "
                      + "stock through checkout, not by writing a number into the ledger."),

        new(
            Id: DeclinedPayment,
            Title: "A declined card gives the unit back",
            Claim: "A card that is refused releases the unit for the next shopper - and the "
                   + "shopper's cart survives so they can try another card.",
            Invariant: "A declined payment releases stock, cancels the order, keeps the cart, and "
                       + "keeps the idempotency key spent.",
            Mechanism: "The gateway call sits BETWEEN two transactions, never inside one. The "
                       + "refusal is applied in the second: release by a guarded UPDATE mirroring "
                       + "the one that took the stock, and cancel the order rather than delete it - "
                       + "because that row is what stops a re-clicked Pay minting a second order "
                       + "number and a second chance at a real charge.",
            ProvenBy: StockRaceTests,
            ProvenByTest: "A_declined_payment_releases_the_unit_for_the_next_shopper",
            Participants: 2,
            Units: 1,
            Creates: "2 throwaway sessions, 2 carts, 2 orders (one Cancelled, one Paid), on a "
                     + "private fixture.",
            Fidelity: "Genuine. The decline is produced by the simulated gateway the shop always "
                      + "uses, driven by the documented Decline scenario hint - the same path a "
                      + "real refusal would take."),

        new(
            Id: DoubleSubmit,
            Title: "A double-clicked Pay button makes one order",
            Claim: "Two identical checkouts fired at the same instant create one order and take one "
                   + "payment. The loser is handed the winner's order, not an error.",
            Invariant: "One order per (session, idempotency key). Two visitors may use the same key "
                       + "without colliding.",
            Mechanism: "Both inserts are allowed to race, and "
                       + "ux_orders_demo_session_id_idempotency_key picks the winner. Not a SELECT "
                       + "first - two simultaneous submits both find nothing and both insert, which "
                       + "is the race rather than the fix. The loser catches the unique violation, "
                       + "rolls back (releasing its own reservations with it) and returns the "
                       + "winner's order with a 200.",
            ProvenBy: IdempotencyTests,
            ProvenByTest: "A_double_clicked_checkout_creates_one_order_and_reserves_one_unit",
            Participants: 2,
            Units: 3,
            Creates: "2 throwaway sessions, 2 carts, 2 orders (one per session, same key), on a "
                     + "private fixture.",
            Fidelity: "Genuine. The two submissions are released on one gate; the third request "
                      + "reuses the same key from a different session to show the key is scoped."),

        new(
            Id: SettlementReplay,
            Title: "A settlement delivered twice pays once",
            Claim: "The payment gateway sends the same signed notification twice. The order is paid "
                   + "once, and the second delivery is answered 200 rather than retried forever.",
            Invariant: "Exactly-once effect from at-least-once delivery. The order row is not "
                       + "written a second time - not even with the same values.",
            Mechanism: "The event id is inserted into processed_webhook_events and the order "
                       + "transition applied in ONE transaction. The second delivery loses on "
                       + "pk_processed_webhook_events and takes the transition down with it. The "
                       + "proof is PostgreSQL's own xmin: the id of the transaction that last wrote "
                       + "the row, unchanged across the duplicate.",
            ProvenBy: SettlementTests,
            ProvenByTest: "A_settlement_delivered_twice_pays_the_order_once",
            Participants: 1,
            Units: 3,
            Creates: "1 throwaway session, 1 cart, 1 order, 1 outbox row, 1 processed-event row, on "
                     + "a private fixture.",
            Fidelity: "Genuine. The bytes redelivered are the gateway's own, read back out of the "
                      + "outbox row checkout wrote and posted verbatim with their stored signature. "
                      + "Nothing is re-serialized and nothing is re-signed."),

        new(
            Id: SettlementRace,
            Title: "Two copies of one settlement, arriving together",
            Claim: "Two copies of one payment notification arrive at the same instant. Exactly one "
                   + "moves the order; the other is told it is a duplicate. Neither gets a 500.",
            Invariant: "The dedupe holds under genuine concurrency, not merely in sequence.",
            Mechanism: "This is the test the primary key exists for. A receiver that asks \"have I "
                       + "seen this?\" before applying passes the sequential case and fails here: "
                       + "two deliveries in flight together both find nothing and both proceed. So "
                       + "there is no such query - the insert races inside the same transaction as "
                       + "the transition, and the loser rolls back.",
            ProvenBy: SettlementTests,
            ProvenByTest: "Two_simultaneous_copies_of_one_settlement_move_the_order_once",
            Participants: 1,
            Units: 2,
            Creates: "1 throwaway session, 1 cart, 1 order, 1 outbox row, 1 processed-event row, on "
                     + "a private fixture.",
            Fidelity: "Genuine. Two deliveries of identical bytes, released on one gate, against the "
                      + "live receiver."),

        new(
            Id: PaymentScenarios,
            Title: "The six payment scenarios, side by side",
            Claim: "One button shows all six ways the simulated gateway can behave: succeed, "
                   + "decline, abandon, duplicate, delay and out-of-order settlement.",
            Invariant: "Every gateway behaviour has a defined, non-exceptional shop response.",
            Mechanism: "PaymentScenarioCatalog selects by explicit hint first and by the amount's "
                       + "trailing cents second. The synchronous three answer inside the checkout "
                       + "request; the asynchronous three answer 202 and enqueue signed "
                       + "notifications in the same transaction that persists the order.",
            ProvenBy: PaymentTests,
            ProvenByTest: "The simulator suite, plus PAYMENT-SCENARIOS.md",
            Participants: 6,
            Units: 6,
            Creates: "6 throwaway sessions, 6 carts, up to 6 orders and their outbox rows, on a "
                     + "private fixture.",
            Fidelity: "Genuine at the moment of checkout - six real checkouts, one per scenario. The "
                      + "three asynchronous ones are NOT followed to settlement here: their "
                      + "notifications are shown sitting in the outbox with their delivery times. "
                      + "Run settlement-replay or settlement-race to follow one all the way."),

        new(
            Id: RefundRace,
            Title: "Twelve refunds of one balance, returned once",
            Claim: "Twelve requests ask for the same order's whole balance back at the same instant, "
                   + "each with its own key so idempotency cannot save it. Exactly one refund "
                   + "happens, eleven are refused, and the ledger holds one row.",
            Invariant: "refunded never exceeds captured, however many refunds overlap - and a row on "
                       + "the refund ledger always means money that actually moved.",
            Mechanism: "SELECT ... FOR UPDATE on the order row, held across the gateway call, so "
                       + "twelve refunds of one order serialize instead of twelve reading the same "
                       + "remaining balance and all passing the check. The CHECK constraint cannot "
                       + "help here and that is the point: each request would write the SAME "
                       + "absolute refunded_amount, so the column ends up correct while twelve "
                       + "refunds have left the building. Only the lock is load-bearing. The ledger "
                       + "row is written after the gateway confirms, never before.",
            ProvenBy: RefundTests,
            ProvenByTest: "Twenty_simultaneous_refunds_of_one_balance_return_it_exactly_once",
            Participants: 12,
            Units: 1,
            Creates: "1 throwaway session, 1 cart, 1 order, 1 reservation and 1 refund, against a "
                     + "private fixture this run creates and destroys.",
            Fidelity: "Genuine. One real checkout, then twelve real refund requests released "
                      + "together on one gate against real PostgreSQL, through the same endpoint the "
                      + "storefront's Refund button calls."),
    ];

    /// <summary>
    /// Finds a scenario by id, case-insensitively.
    /// </summary>
    /// <param name="id">The route value. Trimmed and matched without regard to case, because a
    /// permalink that has been through a chat client or a spreadsheet often is not.</param>
    /// <param name="descriptor">The scenario, when one matches.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> names a scenario.</returns>
    public static bool TryFind(string? id, out DemoLabScenarioDescriptor descriptor)
    {
        descriptor = null!;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmed = id.Trim();

        foreach (var candidate in Descriptors)
        {
            if (string.Equals(candidate.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                descriptor = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Every id, for a 404 that tells the caller what it could have asked for.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Descriptors.Select(descriptor => descriptor.Id)];
}
