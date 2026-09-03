namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// Where an order is, as the storefront understands the server's <c>status</c> string.
/// <para>
/// <see cref="Unknown"/> exists because the wire is a string and this build is not the last one
/// that will ever ship. A status this version has never heard of must render as "something the shop
/// knows and this page does not" rather than silently falling into <see cref="Pending"/>, which
/// would show a shopper a stalled timeline for an order that had moved on.
/// </para>
/// </summary>
public enum OrderStage
{
    /// <summary>A status this build does not recognise.</summary>
    Unknown,

    /// <summary>Created, stock reserved, awaiting payment settlement.</summary>
    Pending,

    /// <summary>Payment captured. Reservations are confirmed at this point.</summary>
    Paid,

    /// <summary>Picked and boxed.</summary>
    Packed,

    /// <summary>Handed to the carrier. Terminal.</summary>
    Shipped,

    /// <summary>Terminal. Reached from Pending or Paid; releases or restocks the units.</summary>
    Cancelled,
}

/// <summary>How one node of the timeline should read.</summary>
public enum OrderStepState
{
    /// <summary>Reached and left behind.</summary>
    Done,

    /// <summary>Where the order is now. The one node that animates.</summary>
    Current,

    /// <summary>Still to come.</summary>
    Ahead,

    /// <summary>Never reached and never will be, because the order was cancelled short of it.</summary>
    Abandoned,
}

/// <summary>
/// One node of the timeline, already resolved into what the component has to draw.
/// </summary>
/// <param name="Stage">The stage this node represents.</param>
/// <param name="Label">Its name on screen.</param>
/// <param name="Description">One line saying what actually happened at this step, in the shop's own terms.</param>
/// <param name="State">How to draw it.</param>
/// <param name="At">The timestamp to show, or null when there is none to show.</param>
/// <param name="AtIsObserved">
/// True when <paramref name="At"/> is the moment this page watched the change happen rather than a
/// time the server recorded. The distinction is shown in the UI, not smoothed over: the API stores
/// <c>placed_at</c> and <c>paid_at</c> and nothing else, so a packing time presented as though it
/// came from the order would be a number this page invented.
/// </param>
public sealed record OrderTimelineNode(
    OrderStage Stage,
    string Label,
    string Description,
    OrderStepState State,
    DateTimeOffset? At,
    bool AtIsObserved);

/// <summary>
/// Turning an order into a timeline, and deciding how often to ask the server whether it has moved.
/// <para>
/// Kept out of the component so both are testable as plain functions and so the polling schedule —
/// the part with a real cost attached, since every poll keeps a serverless database awake — is
/// stated once, in prose, rather than buried in a timer somewhere.
/// </para>
/// </summary>
public static class OrderProgress
{
    /// <summary>The four steps of the happy path, in order. Cancelled is not one of them: it is a departure from the path, not a point on it.</summary>
    private static readonly OrderStage[] Path = [OrderStage.Pending, OrderStage.Paid, OrderStage.Packed, OrderStage.Shipped];

    /// <summary>
    /// Reads the server's status string. Case-insensitive, and anything unrecognised is
    /// <see cref="OrderStage.Unknown"/> rather than a guess.
    /// </summary>
    public static OrderStage Parse(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "PENDING" => OrderStage.Pending,
        "PAID" => OrderStage.Paid,
        "PACKED" => OrderStage.Packed,
        "SHIPPED" => OrderStage.Shipped,
        "CANCELLED" => OrderStage.Cancelled,
        _ => OrderStage.Unknown,
    };

    /// <summary>
    /// True when the order will not move again on its own, so there is nothing left to poll for.
    /// <para>
    /// <see cref="OrderStage.Unknown"/> is treated as terminal too. A page that cannot tell what a
    /// status means also cannot tell whether it is going to change, and polling forever on the
    /// strength of not understanding the answer is the wrong way to be wrong.
    /// </para>
    /// </summary>
    public static bool IsTerminal(OrderStage stage) =>
        stage is OrderStage.Shipped or OrderStage.Cancelled or OrderStage.Unknown;

    /// <summary>
    /// A one-line, shopper-facing name for a stage.
    /// </summary>
    public static string Label(OrderStage stage) => stage switch
    {
        OrderStage.Pending => "Placed",
        OrderStage.Paid => "Paid",
        OrderStage.Packed => "Packed",
        OrderStage.Shipped => "Shipped",
        OrderStage.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    /// <summary>
    /// The four nodes to draw, resolved against the order's current stage.
    /// </summary>
    /// <param name="stage">Where the order is now.</param>
    /// <param name="placedAt">The server's <c>placedAt</c>.</param>
    /// <param name="paidAt">The server's <c>paidAt</c>, or null while the order is unpaid.</param>
    /// <param name="observed">
    /// Stages this page saw arrive, and when. Supplies the only timestamp available for Packed and
    /// Shipped, which the API records no column for.
    /// </param>
    public static IReadOnlyList<OrderTimelineNode> Nodes(
        OrderStage stage,
        DateTimeOffset placedAt,
        DateTimeOffset? paidAt,
        IReadOnlyDictionary<OrderStage, DateTimeOffset>? observed = null)
    {
        // An order cancelled from Pending never reached Paid; one cancelled after settlement did,
        // and paid_at is the durable proof of it. That single column is enough to draw a cancelled
        // order's history correctly without the API growing a status log.
        var reached = stage == OrderStage.Cancelled
            ? (paidAt is null ? 0 : 1)
            : Array.IndexOf(Path, stage);

        var nodes = new List<OrderTimelineNode>(Path.Length);

        for (var index = 0; index < Path.Length; index++)
        {
            var step = Path[index];

            var state = stage switch
            {
                OrderStage.Cancelled => index <= reached ? OrderStepState.Done : OrderStepState.Abandoned,
                OrderStage.Unknown => OrderStepState.Ahead,
                _ when index < reached => OrderStepState.Done,
                _ when index == reached => OrderStepState.Current,
                _ => OrderStepState.Ahead,
            };

            var (at, isObserved) = Timestamp(step, placedAt, paidAt, observed, state);

            nodes.Add(new OrderTimelineNode(step, Label(step), Description(step, state), state, at, isObserved));
        }

        return nodes;
    }

    /// <summary>
    /// How long to wait before asking again, or null to stop asking.
    ///
    /// <para>
    /// <strong>The schedule, and why it is shaped like this.</strong> The demo's own timeline moves
    /// an order from Paid to Packed after twenty seconds and to Shipped forty seconds after that, so
    /// the whole story is over inside a minute and three seconds is fine-grained enough that no
    /// transition is stale by more than a few percent of its dwell. After that, the interesting case
    /// is over: an order still moving at ten minutes is one whose payment is settling late, and an
    /// order still Pending at half an hour is almost certainly the <c>Abandon</c> scenario, which
    /// will stay Pending until its reservation lapses and no amount of asking will change it.
    /// </para>
    /// <para>
    /// <strong>Stopping is the important part.</strong> A page left open on a laptop lid overnight
    /// would otherwise keep a serverless Postgres awake until morning, on a plan billed by
    /// compute-hour, to re-read a row that is never going to change. So the polling ends and the
    /// page offers a refresh button instead, which costs one request when someone actually wants it.
    /// </para>
    /// </summary>
    /// <param name="stage">Where the order is now.</param>
    /// <param name="watched">How long this page has been watching it.</param>
    public static TimeSpan? NextPollDelay(OrderStage stage, TimeSpan watched)
    {
        if (IsTerminal(stage))
            return null;

        if (watched < TimeSpan.FromMinutes(2))
            return TimeSpan.FromSeconds(3);

        if (watched < TimeSpan.FromMinutes(10))
            return TimeSpan.FromSeconds(8);

        if (watched < TimeSpan.FromMinutes(30))
            return TimeSpan.FromSeconds(30);

        return null;
    }

    private static (DateTimeOffset? At, bool IsObserved) Timestamp(
        OrderStage step,
        DateTimeOffset placedAt,
        DateTimeOffset? paidAt,
        IReadOnlyDictionary<OrderStage, DateTimeOffset>? observed,
        OrderStepState state)
    {
        // A step that has not happened has no time, and inventing an expected one would mean
        // duplicating the server's dwell configuration in the browser — where it would be wrong for
        // anyone who changed Fulfilment:Timeline.
        if (state is OrderStepState.Ahead or OrderStepState.Abandoned)
            return (null, false);

        switch (step)
        {
            case OrderStage.Pending:
                return (placedAt, false);

            case OrderStage.Paid when paidAt is not null:
                return (paidAt, false);

            default:
                // Packed and Shipped have no persisted timestamp — the order table stores placed_at
                // and paid_at and nothing else. The moment this page watched the change is the only
                // honest thing left to show, and it is labelled as such. A shopper opening the link
                // tomorrow watched nothing, so they correctly get no time at all.
                return observed is not null && observed.TryGetValue(step, out var seen)
                    ? (seen, true)
                    : (null, false);
        }
    }

    private static string Description(OrderStage step, OrderStepState state) => step switch
    {
        OrderStage.Pending => state == OrderStepState.Current
            ? "The order exists and its stock is reserved. Payment has not settled yet."
            : "The order was created and its stock reserved.",
        OrderStage.Paid => state switch
        {
            OrderStepState.Current => "The money has moved and the reservation is confirmed.",
            OrderStepState.Abandoned => "Never settled. The order was cancelled before payment.",
            _ => "Payment captured and the reservation confirmed.",
        },
        OrderStage.Packed => state == OrderStepState.Abandoned
            ? "Not reached."
            : "Picked from the shelf and boxed.",
        OrderStage.Shipped => state == OrderStepState.Abandoned
            ? "Not reached."
            : "Handed to the carrier. The units have left the shelf for good.",
        _ => "",
    };
}
