using System.Globalization;

namespace VelaCommerce.Storefront.Catalog;

/// <summary>
/// Number formatting for generated SVG geometry and inline CSS lengths.
/// <para>
/// WebAssembly picks up the browser's culture, so interpolating a <see cref="double"/>
/// straight into markup renders "12,5" for a French shopper and the path, or the CSS rule,
/// is silently discarded. Everything geometric goes through here so the invariant culture
/// is not something each component has to remember.
/// </para>
/// </summary>
public static class Svg
{
    /// <summary>Formats a coordinate or length for SVG, invariantly and to two decimals.</summary>
    public static string N(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Formats an integer invariantly, for attributes such as tick counts.</summary>
    public static string N(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a value as a CSS <c>rem</c> length, invariantly.</summary>
    public static string Rem(double value) =>
        string.Concat(N(value), "rem");

    /// <summary>Formats a value as a CSS <c>deg</c> angle, invariantly.</summary>
    public static string Deg(double value) =>
        string.Concat(N(value), "deg");
}
