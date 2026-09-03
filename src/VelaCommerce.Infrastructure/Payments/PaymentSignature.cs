using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VelaCommerce.Infrastructure.Payments;

/// <summary>
/// The one place that knows how a settlement notification is signed and verified.
/// <para>
/// Signing and verifying are the same three lines read in opposite directions, and the classic
/// way to get this wrong is to write them twice — once in the sender and once in the receiver —
/// and let them drift by a separator, a case convention or an encoding. Both halves live here so
/// that a change to the scheme is impossible to apply to only one side, and so that the tests can
/// exercise the exact code the receiver runs.
/// </para>
/// <para><b>The scheme.</b> Modelled on what mainstream gateways actually send:</para>
/// <list type="bullet">
/// <item>The signed message is <c>{unix-seconds}.{raw-payload-bytes}</c>.</item>
/// <item>The MAC is HMAC-SHA256 under the shared secret, rendered as lowercase hex.</item>
/// <item>The header is <c>t={unix-seconds},v1={hex}</c>, under <see cref="HeaderName"/>.</item>
/// </list>
/// <para>
/// Two properties of that shape are load-bearing. Binding the timestamp <em>into</em> the MAC is
/// what makes the replay window enforceable — a signature lifted from a log cannot be re-dated,
/// because changing <c>t</c> invalidates the hash. And the <c>v1</c> label is what makes the
/// scheme replaceable: a future <c>v2</c> can be sent alongside <c>v1</c> and receivers migrated
/// one at a time, instead of every deployment having to cut over in the same second.
/// </para>
/// <para>
/// <b>Verify over the raw request body.</b> The bytes that arrive must be hashed as they arrive.
/// Deserializing to an object and re-serializing it produces a payload that differs in whitespace,
/// property order or number formatting, and the signature stops matching for reasons that look
/// like a security incident and are not. In ASP.NET Core that means enabling buffering and reading
/// the body as bytes before model binding gets to it.
/// </para>
/// </summary>
public static class PaymentSignature
{
    /// <summary>The request header carrying the signature.</summary>
    public const string HeaderName = "X-Vela-Signature";

    /// <summary>Current scheme label. Present so a second scheme can be introduced without a flag day.</summary>
    public const string SchemeVersion = "v1";

    /// <summary>Length of a lowercase-hex SHA-256 MAC: 32 bytes rendered as two characters each.</summary>
    private const int HexLength = 64;

    private const string TimestampField = "t";

    /// <summary>
    /// Signs <paramref name="payload"/> and returns the complete header value.
    /// </summary>
    /// <param name="payload">The exact bytes that will be transmitted as the body.</param>
    /// <param name="signedAt">
    /// The instant to bind into the signature. A parameter rather than a clock read, so that
    /// signing a fixed payload at a fixed instant is reproducible byte for byte — which is what
    /// lets a test assert on a literal signature instead of only on a round trip.
    /// </param>
    /// <param name="secret">The shared secret. Never logged, never included in the header.</param>
    public static string CreateHeader(ReadOnlySpan<byte> payload, DateTimeOffset signedAt, string secret)
    {
        var timestamp = signedAt.ToUnixTimeSeconds();
        var signature = Compute(payload, timestamp, secret);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{TimestampField}={timestamp},{SchemeVersion}={signature}");
    }

    /// <summary>
    /// The MAC on its own, as lowercase hex. Exposed for tests and for anyone building a second
    /// transport; ordinary senders want <see cref="CreateHeader"/>.
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> payload, long unixTimeSeconds, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // "{timestamp}." then the payload, assembled once so the MAC covers both in one pass.
        var prefix = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{unixTimeSeconds}."));

        var message = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(message.AsSpan());
        payload.CopyTo(message.AsSpan(prefix.Length));

        var key = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant();
        }
        finally
        {
            // The secret already exists as a managed string we cannot scrub, so this is hygiene
            // rather than a guarantee. It still removes one long-lived copy from a heap that may
            // end up in a crash dump.
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Verifies a received header against the raw body.
    /// </summary>
    /// <param name="payload">The raw request body, exactly as received. Not re-serialized.</param>
    /// <param name="headerValue">The value of <see cref="HeaderName"/>, or <see langword="null"/> if absent.</param>
    /// <param name="secret">The shared secret.</param>
    /// <param name="now">The receiver's current instant, passed in rather than read from the ambient clock.</param>
    /// <param name="tolerance">How far <c>t</c> may be from <paramref name="now"/>, in either direction.</param>
    public static PaymentSignatureResult Verify(
        ReadOnlySpan<byte> payload,
        string? headerValue,
        string secret,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (!TryParseHeader(headerValue, out var timestamp, out var provided))
            return PaymentSignatureResult.Malformed;

        // Checked before the MAC so that a stale replay is reported as stale rather than as a
        // mismatch. Both directions: a timestamp far in the future is skew or forgery, not freshness.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age > tolerance || age < -tolerance)
            return PaymentSignatureResult.Expired;

        return FixedTimeEquals(Compute(payload, timestamp, secret), provided)
            ? PaymentSignatureResult.Valid
            : PaymentSignatureResult.Mismatched;
    }

    /// <summary>
    /// Compares two hex MACs without leaking, through timing, how many leading characters matched.
    /// <para>
    /// Public so that nothing anywhere else has to reach for <c>==</c> on a signature.
    /// <c>string.Equals</c> returns on the first differing character, which over enough requests
    /// lets an attacker recover a valid signature one character at a time. That attack is
    /// impractical over the public internet and trivial from inside the same host, and the fix
    /// costs one method call, so there is no version of this worth arguing about.
    /// </para>
    /// <para>
    /// Length is not secret here — a SHA-256 MAC is always 64 hex characters — so returning early
    /// on a wrong length leaks nothing. Case is normalised first, so an upper-case hex signature
    /// from another implementation verifies.
    /// </para>
    /// </summary>
    public static bool FixedTimeEquals(string expectedHex, string? providedHex)
    {
        ArgumentNullException.ThrowIfNull(expectedHex);

        if (providedHex is null || providedHex.Length != expectedHex.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(providedHex.ToLowerInvariant()));
    }

    /// <summary>
    /// Splits <c>t=…,v1=…</c> into its parts. Unknown fields are ignored rather than rejected, so
    /// that a sender adding a <c>v2=</c> alongside <c>v1=</c> does not break a receiver that only
    /// understands <c>v1</c> — which is the entire reason the fields are labelled.
    /// </summary>
    public static bool TryParseHeader(string? headerValue, out long unixTimeSeconds, out string signature)
    {
        unixTimeSeconds = 0;
        signature = string.Empty;

        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var seenTimestamp = false;

        foreach (var range in headerValue.AsSpan().Split(','))
        {
            var field = headerValue.AsSpan()[range].Trim();
            var separator = field.IndexOf('=');
            if (separator <= 0)
                return false;

            var name = field[..separator];
            var value = field[(separator + 1)..];

            if (name.Equals(TimestampField, StringComparison.Ordinal))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out unixTimeSeconds))
                    return false;

                seenTimestamp = true;
            }
            else if (name.Equals(SchemeVersion, StringComparison.Ordinal))
            {
                if (value.Length != HexLength || !IsHex(value))
                    return false;

                signature = value.ToString();
            }
        }

        return seenTimestamp && signature.Length == HexLength;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }

        return true;
    }
}
