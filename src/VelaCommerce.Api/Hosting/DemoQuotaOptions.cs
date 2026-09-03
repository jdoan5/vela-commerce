using System.Globalization;

namespace VelaCommerce.Api.Hosting;

/// <summary>
/// How many rows one visitor may leave behind in a shared demo database.
/// <para>
/// Rate limiting bounds how <em>fast</em> somebody can write; this bounds how <em>much</em> they
/// can accumulate. The two are not substitutes. A patient script writing one row a second, well
/// inside every limiter here, still fills a free-tier database in a weekend — and the reason to
/// care is not disk space but that the demo is the portfolio: an unavailable database is a broken
/// link on a CV.
/// </para>
/// <para>
/// The numbers are generous enough that no reviewer will ever meet one. A cart of forty distinct
/// SKUs and twenty-five orders is far past what anybody exploring a shop does, and the ceiling
/// exists for the case where somebody is not exploring.
/// </para>
/// </summary>
/// <param name="MaxCartsPerSession">
/// Cart rows one session may own. Effectively a tripwire rather than a working limit: the cart
/// endpoint creates a row only when the session has none, so the only way past one is the
/// documented two-carts race, and the index on <c>demo_session_id</c> is deliberately not unique.
/// It is enforced anyway, because "no endpoint can currently do that" is a fact about today's
/// endpoints and not a property of the data.
/// </param>
/// <param name="MaxLinesPerCart">
/// Lines in the cart a shopper is adding to. This is the cap that does real work: a line is one
/// row per distinct variant, the catalog holds several hundred, and nothing about the domain stops
/// a script adding every one of them. Quantity is already capped at 99 per line by
/// <c>CartLine.MaxQuantity</c>, so this is the other axis.
/// </param>
/// <param name="MaxOrdersPerSession">
/// Orders one session may place. The expensive cap: every order drags order lines, stock
/// reservations and — for the asynchronous payment scenarios — outbox rows behind it, so an order
/// is worth several rows and a settlement round trip.
/// </param>
internal sealed record DemoQuotaOptions(
    int MaxCartsPerSession,
    int MaxLinesPerCart,
    int MaxOrdersPerSession)
{
    /// <summary>Configuration section. Colon-separated, matching every other option group in this solution.</summary>
    public const string SectionName = "Demo:Quotas";

    /// <summary>The shipped numbers, used whenever configuration is absent or unusable.</summary>
    public static DemoQuotaOptions Defaults { get; } = new(
        MaxCartsPerSession: 5,
        MaxLinesPerCart: 40,
        MaxOrdersPerSession: 25);

    /// <summary>
    /// Reads the section, falling back per key to the default for anything absent or unusable.
    /// <para>
    /// Hand-bound and deliberately incapable of throwing, matching <c>PaymentSimulatorOptions</c>
    /// and <c>OutboxOptions</c>. Build-time OpenAPI generation composes this host, so refusing to
    /// start over a mistyped quota would turn a harmless configuration slip into a red build.
    /// </para>
    /// </summary>
    public static DemoQuotaOptions Read(IConfiguration? configuration, ILogger? logger) => new(
        ReadPositive(configuration, logger, nameof(MaxCartsPerSession), Defaults.MaxCartsPerSession),
        ReadPositive(configuration, logger, nameof(MaxLinesPerCart), Defaults.MaxLinesPerCart),
        ReadPositive(configuration, logger, nameof(MaxOrdersPerSession), Defaults.MaxOrdersPerSession));

    private static int ReadPositive(IConfiguration? configuration, ILogger? logger, string key, int fallback)
    {
        var configured = configuration?[$"{SectionName}:{key}"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        logger?.LogWarning(
            "{Key} is '{Value}', which is not a positive whole number. Falling back to {Fallback}.",
            $"{SectionName}:{key}",
            configured,
            fallback);

        return fallback;
    }
}
