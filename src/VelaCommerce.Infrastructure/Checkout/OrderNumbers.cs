using System.Diagnostics.CodeAnalysis;

namespace VelaCommerce.Infrastructure.Checkout;

/// <summary>
/// The human-facing order reference: <c>VELA-</c> plus seven Crockford Base32 characters.
/// <para>
/// <strong>Uniqueness is guaranteed by construction, not by luck.</strong> The input is a value
/// from the PostgreSQL sequence <c>order_number_seq</c>, and <c>nextval</c> never returns the same
/// value twice — not to two concurrent transactions, not after a rollback, not after a crash.
/// <see cref="Format"/> then runs that value through a <em>bijection</em> on the 35-bit integers,
/// so two different sequence values cannot possibly produce the same seven characters. There is no
/// birthday problem here to reason about and no retry loop to get wrong, which is the whole reason
/// the number is derived from a counter rather than sampled from randomness.
/// </para>
/// <para>
/// <strong>Why not a truncated GUID.</strong> An earlier attempt cut the first characters off a
/// UUIDv7 and collided on 87.5% of same-millisecond pairs, because the leading bits of a v7 are a
/// timestamp: truncating one keeps the part that is shared and discards the part that is random.
/// Truncating the <em>tail</em> instead trades that for a birthday collision — 7 base32 characters
/// is 35 bits, so random values would start colliding after roughly 2^17.5 ≈ 185,000 orders, which
/// is few enough to actually happen and rare enough to only happen in production.
/// </para>
/// <para>
/// <strong>Why the number is scrambled at all.</strong> A sequence printed straight would publish
/// the store's order count on every confirmation page — order 41 tells a visitor exactly how much
/// has ever been sold. Multiplying by an odd constant modulo 2^35 and xor-shifting are both
/// invertible operations, so the output looks unrelated to its neighbours while remaining
/// collision-free. This is obfuscation, not encryption: anybody determined enough can recover the
/// counter, and nothing security-relevant may depend on them not doing so.
/// </para>
/// <para>
/// The alphabet is Crockford's: no <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>, so <c>1</c> and
/// <c>0</c> cannot be misread, and no word made of these letters can be read as an obscenity.
/// <see cref="TryNormalize"/> accepts what a human would type back — lowercase, and the letters
/// Crockford excluded — so an order number read off a screen and into a support chat still finds
/// its order.
/// </para>
/// </summary>
public static class OrderNumbers
{
    /// <summary>
    /// The database sequence the input comes from. Duplicated as a literal in the migration that
    /// creates it, deliberately: a migration must keep working after this constant is renamed or
    /// deleted, so migrations never reference application code.
    /// </summary>
    public const string SequenceName = "order_number_seq";

    /// <summary>Marks the string as ours in a log line or a support ticket full of other references.</summary>
    public const string Prefix = "VELA-";

    /// <summary>Seven characters at five bits each: exactly the 35 bits the mixing function works over.</summary>
    public const int PayloadLength = 7;

    /// <summary>
    /// Crockford Base32. The four omitted letters are the ones that get misread aloud or on paper.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const ulong Mask = (1UL << 35) - 1UL;

    /// <summary>
    /// The largest sequence value this format can carry without wrapping. Beyond it two sequence
    /// values would share an order number, so <see cref="Format"/> throws rather than emit one:
    /// 34 billion orders is not a limit this demo will reach, but a silent wrap would be a
    /// duplicate reference and duplicate references are exactly what this type exists to prevent.
    /// </summary>
    public const long MaxSequenceValue = (1L << 35) - 1L;

    // Two odd constants, so each multiplication is invertible modulo 2^35 (an odd number has a
    // multiplicative inverse there; an even one does not, and would collapse distinct inputs onto
    // the same output). Their exact values are arbitrary — only oddness is load-bearing.
    private const ulong MultiplierA = 0x3_9B0F_4A6DUL;
    private const ulong MultiplierB = 0x5_C7E1_9A3BUL;

    /// <summary>Prefix plus payload. Fits the <c>varchar(32)</c> the orders table gives it, with room to spare.</summary>
    public static int TotalLength { get; } = Prefix.Length + PayloadLength;

    /// <summary>
    /// Renders a sequence value as an order number.
    /// </summary>
    /// <param name="sequenceValue">
    /// A value from <see cref="SequenceName"/>. Must be in <c>[1, <see cref="MaxSequenceValue"/>]</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside the range the format can represent injectively.
    /// </exception>
    public static string Format(long sequenceValue)
    {
        if (sequenceValue is < 1 or > MaxSequenceValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceValue),
                sequenceValue,
                $"Order numbers are minted from {SequenceName} values in [1, {MaxSequenceValue}]. "
                + "Outside that range the encoding stops being injective, which would mean two "
                + "orders sharing a reference.");
        }

        var mixed = Mix((ulong)sequenceValue);

        return string.Create(TotalLength, mixed, static (destination, value) =>
        {
            Prefix.CopyTo(destination);

            // Least-significant five bits first, written right to left, so the characters read in
            // the same order as the bits they encode.
            for (var index = PayloadLength - 1; index >= 0; index--)
            {
                destination[Prefix.Length + index] = Alphabet[(int)(value & 31UL)];
                value >>= 5;
            }
        });
    }

    /// <summary>
    /// Accepts an order number as a human would retype it and returns the canonical form.
    /// <para>
    /// Case is folded, and Crockford's three confusable letters are mapped onto the digits they
    /// resemble (<c>O</c> to <c>0</c>, <c>I</c> and <c>L</c> to <c>1</c>). That substitution is
    /// unambiguous precisely because those letters are not in the alphabet, so it can never turn
    /// one valid order number into a different valid one.
    /// </para>
    /// <para>
    /// Called before the database is, so an obviously malformed reference costs no round trip —
    /// and, more usefully, so the lookup compares against a canonical string rather than relying
    /// on a case-insensitive index that does not exist.
    /// </para>
    /// </summary>
    public static bool TryNormalize(string? candidate, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        if (candidate is null)
        {
            return false;
        }

        var trimmed = candidate.Trim();

        if (trimmed.Length != TotalLength
            || !trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<char> buffer = stackalloc char[TotalLength];
        Prefix.CopyTo(buffer);

        for (var index = 0; index < PayloadLength; index++)
        {
            var character = char.ToUpperInvariant(trimmed[Prefix.Length + index]);

            character = character switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => character,
            };

            if (!Alphabet.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }

            buffer[Prefix.Length + index] = character;
        }

        normalized = new string(buffer);
        return true;
    }

    /// <summary>
    /// The bijection. Every step below is individually invertible over the 35-bit integers, so
    /// their composition is too:
    /// <list type="bullet">
    /// <item><c>x ^ (x >> k)</c> is invertible for any <c>k >= 1</c> — it is unit lower-triangular
    /// over GF(2), so it can be undone by repeated shifting.</item>
    /// <item><c>x * odd</c> modulo a power of two is invertible, because an odd number is a unit
    /// in that ring.</item>
    /// </list>
    /// The multiplications are <c>unchecked</c> because they are meant to wrap: arithmetic modulo
    /// 2^64 followed by a mask to 35 bits is the same value as arithmetic modulo 2^35, since 2^35
    /// divides 2^64.
    /// </summary>
    private static ulong Mix(ulong value)
    {
        value &= Mask;

        value ^= value >> 18;
        value = unchecked(value * MultiplierA) & Mask;
        value ^= value >> 21;
        value = unchecked(value * MultiplierB) & Mask;
        value ^= value >> 14;

        return value;
    }
}
