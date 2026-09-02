using System.Text;

namespace VelaCommerce.SeedGen;

/// <summary>
/// Turns a display name into a URL segment.
/// <para>
/// Lives here rather than in the domain because slugs are a presentation concern: the
/// domain only insists a slug is non-empty and lower-case. Uses ordinal/invariant casing
/// so a machine with a Turkish locale produces the same bytes as one with en-US, which is
/// the whole point of this generator being deterministic.
/// </para>
/// </summary>
internal static class Slug
{
    public static string From(string value)
    {
        var builder = new StringBuilder(value.Length);

        // Starting "already hyphenated" suppresses a leading separator.
        var separatorPending = true;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
                separatorPending = false;
            }
            else if (!separatorPending)
            {
                builder.Append('-');
                separatorPending = true;
            }
        }

        return builder.ToString().TrimEnd('-');
    }
}
