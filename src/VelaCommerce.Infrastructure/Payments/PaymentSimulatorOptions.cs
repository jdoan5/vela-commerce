using System.Text;
using Microsoft.Extensions.Configuration;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// Configuration for the in-repository gateway, with a development default for every value so a
/// fresh clone runs a complete purchase with no configuration at all.
/// <para>
/// That default is the point of the whole slice: the README promises <c>git clone</c> then
/// <c>dotnet run</c>, and a required secret would turn that into a support conversation. The
/// price is that the shipped signing secret is public, which is fine for a simulator whose
/// "money" is imaginary and fatal for anything else — hence <see cref="UsesDevelopmentSecret"/>
/// and the startup warning it drives.
/// </para>
/// </summary>
public sealed record PaymentSimulatorOptions
{
    /// <summary>Configuration section this binds from. Colon-separated: <c>Payments:Simulator</c>.</summary>
    public const string SectionName = "Payments:Simulator";

    /// <summary>
    /// The secret used when configuration supplies none.
    /// <para>
    /// Committed on purpose, and named so that it cannot be mistaken for a real one in a log, a
    /// screenshot or a search of the repository. Production must override it — see
    /// <see cref="Validate"/> — and the value there belongs in an environment variable or a key
    /// vault reference, never in <c>appsettings.Production.json</c>, because that file ships
    /// inside the container image.
    /// </para>
    /// </summary>
    public const string DevelopmentSigningSecret = "vela-development-only-signing-secret-not-for-production";

    /// <summary>
    /// Shared secret for the HMAC-SHA256 signature on settlement notifications. Never logged:
    /// <see cref="PrintMembers"/> redacts it, so even <c>logger.LogInformation("{Options}", options)</c>
    /// cannot leak it.
    /// </summary>
    public string SigningSecret { get; init; } = DevelopmentSigningSecret;

    /// <summary>
    /// Prefix on generated gateway references, so a value in a log is identifiable at a glance as
    /// simulated rather than real. Changing it changes every reference the simulator will ever
    /// produce, which is why it is configuration and not a constant.
    /// </summary>
    public string GatewayReferencePrefix { get; init; } = "sim";

    /// <summary>
    /// How long a deferred settlement waits before it is delivered. Three seconds is long enough
    /// for a reviewer to watch the "confirming payment" state appear and short enough that they
    /// do not think it has hung.
    /// </summary>
    public TimeSpan SettlementDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How far a notification's timestamp may be from now before the receiver rejects it. This is
    /// the replay window: a signature captured from a log is worthless once it falls outside.
    /// Five minutes matches what mainstream gateways document, and is generous enough to survive
    /// a container that scaled to zero and came back with modest clock skew.
    /// </summary>
    public TimeSpan SignatureTolerance { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the trailing cents of the amount may select a scenario when no explicit hint is
    /// given. See <see cref="PaymentScenarioCatalog"/> for the mapping.
    /// <para>
    /// Default on, and the tradeoff is real: an order whose total genuinely ends in <c>.03</c>
    /// will duplicate its webhook. That is acceptable for a demo whose stated purpose is showing
    /// duplicate webhooks being handled correctly, and it is why the flag exists — a deployment
    /// that wants scenarios driven only by an explicit hint turns it off here rather than by
    /// editing the selector.
    /// </para>
    /// </summary>
    public bool RecogniseMagicAmounts { get; init; } = false;

    /// <summary>True while the committed development secret is still in use.</summary>
    public bool UsesDevelopmentSecret =>
        string.Equals(SigningSecret, DevelopmentSigningSecret, StringComparison.Ordinal);

    /// <summary>
    /// Reads the section, falling back to the development default for anything absent or
    /// unparseable-as-configured. Bound by hand rather than through
    /// <c>Microsoft.Extensions.Options.ConfigurationExtensions</c> so that Infrastructure does not
    /// take a package reference for a single call; the tradeoff is that a malformed value throws
    /// here, at startup, with the key name in the message, instead of silently binding to default.
    /// </summary>
    public static PaymentSimulatorOptions FromConfiguration(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var defaults = new PaymentSimulatorOptions();

        return new PaymentSimulatorOptions
        {
            SigningSecret = Read(section, nameof(SigningSecret)) ?? defaults.SigningSecret,
            GatewayReferencePrefix = Read(section, nameof(GatewayReferencePrefix)) ?? defaults.GatewayReferencePrefix,
            SettlementDelay = ReadTimeSpan(section, nameof(SettlementDelay), defaults.SettlementDelay),
            SignatureTolerance = ReadTimeSpan(section, nameof(SignatureTolerance), defaults.SignatureTolerance),
            RecogniseMagicAmounts = ReadBoolean(section, nameof(RecogniseMagicAmounts), defaults.RecogniseMagicAmounts)
        };
    }

    /// <summary>
    /// Fails fast on a configuration that cannot work. Called from
    /// <c>AddPaymentSimulator</c> at registration, so a bad value stops the host starting rather
    /// than surfacing as an unverifiable signature on the first webhook.
    /// </summary>
    /// <param name="isDevelopment">
    /// Whether the host is running in Development. In every other environment the committed
    /// secret is refused outright: shipping it to production would let anyone who has read this
    /// public repository forge a settlement and mark an order paid.
    /// </param>
    /// <summary>
    /// Refuses to take money with a secret that anyone can read.
    /// <para>
    /// Called on the paths that actually matter — authorizing a payment and verifying a
    /// settlement notification — rather than at startup, so that merely composing the host
    /// (which the OpenAPI generator does at build time, as Production) cannot fail on it.
    /// </para>
    /// </summary>
    public void AssertUsable(bool isDevelopment)
    {
        if (!isDevelopment && UsesDevelopmentSecret)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SigningSecret)} is still the committed development default outside Development. "
                + "That value is public in this repository, so anyone could forge a settlement notification. Supply a "
                + "real secret through an environment variable or a key vault reference.");
    }

    public void Validate(bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(SigningSecret))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SigningSecret)} is empty. Set it, or remove the key to take the development default.");

        if (SigningSecret.Length < 32)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SigningSecret)} is {SigningSecret.Length} characters. HMAC-SHA256 keys shorter "
                + "than the 32-byte block are the one case where key length genuinely reduces security here; use at "
                + "least 32 characters.");

        // The committed-default check is deliberately NOT here. It is enforced where it bites —
        // AssertUsable, called before authorizing a payment or verifying a notification — because
        // startup also happens under the build-time OpenAPI generator, which runs this entry point
        // with no environment set and therefore looks like Production. Refusing to boot there
        // turned a deployment safeguard into a broken build, while a host that never takes a
        // payment was never at risk in the first place.

        if (string.IsNullOrWhiteSpace(GatewayReferencePrefix))
            throw new InvalidOperationException($"{SectionName}:{nameof(GatewayReferencePrefix)} must not be empty.");

        if (SettlementDelay < TimeSpan.Zero)
            throw new InvalidOperationException($"{SectionName}:{nameof(SettlementDelay)} must not be negative.");

        if (SignatureTolerance <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SignatureTolerance)} must be positive; a zero window rejects every notification, "
                + "including the ones we just signed.");

        // These two are not independent. A notification is signed at the authorization instant and
        // delivered SettlementDelay later, so a delay at or beyond the tolerance guarantees every
        // deferred settlement arrives already expired — the demo would look like a signature
        // vulnerability rather than a misconfiguration. Caught here, once, with the arithmetic
        // spelled out, instead of at 3am in a webhook log.
        if (SettlementDelay >= SignatureTolerance)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SettlementDelay)} ({SettlementDelay}) must be shorter than "
                + $"{nameof(SignatureTolerance)} ({SignatureTolerance}). Settlements are signed when the payment is "
                + "authorized and delivered after the delay, so a delay that reaches the tolerance means every "
                + "deferred notification arrives outside its own replay window and is rejected.");
    }

    /// <summary>
    /// Redacts the secret from the compiler-generated <c>ToString</c>.
    /// <para>
    /// Records print every member by default, so an options object reaching a structured log —
    /// through a scope, an exception message, a debugger's watch window that someone screenshots —
    /// would carry the signing key with it. Overriding the print hook is the only place to stop
    /// that once and for all; a convention that "nobody logs the options" holds until the first
    /// person who does.
    /// </para>
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        // Private, not protected override: the record is sealed, so the compiler emits the hook as
        // private and an `override` here would not compile.
        builder.Append("SigningSecret = <redacted:")
            .Append(UsesDevelopmentSecret ? "development-default" : "configured")
            .Append(">, GatewayReferencePrefix = ").Append(GatewayReferencePrefix)
            .Append(", SettlementDelay = ").Append(SettlementDelay)
            .Append(", SignatureTolerance = ").Append(SignatureTolerance)
            .Append(", RecogniseMagicAmounts = ").Append(RecogniseMagicAmounts);

        return true;
    }

    private static string? Read(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan ReadTimeSpan(IConfiguration section, string key, TimeSpan fallback)
    {
        var value = Read(section, key);
        if (value is null) return fallback;

        return TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"{SectionName}:{key} is '{value}', which is not a TimeSpan. Use the invariant form, e.g. '00:00:03'.");
    }

    private static bool ReadBoolean(IConfiguration section, string key, bool fallback)
    {
        var value = Read(section, key);
        if (value is null) return fallback;

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{SectionName}:{key} is '{value}', which is not true or false.");
    }
}
