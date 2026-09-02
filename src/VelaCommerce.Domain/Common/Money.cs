using System.Globalization;

namespace VelaCommerce.Domain.Common;

/// <summary>
/// A currency amount held in minor units (cents), never a floating-point type.
/// Arithmetic across differing currencies is refused rather than silently coerced.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public const string DefaultCurrency = "USD";

    public long Amount { get; }
    public string Currency { get; }

    public Money(long amount, string currency = DefaultCurrency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new DomainException($"Currency must be a 3-letter ISO code, got '{currency}'.");

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency = DefaultCurrency) => new(0, currency);

    /// <summary>Builds a Money from a major-unit decimal (e.g. 19.99m -> 1999 cents).</summary>
    public static Money FromDecimal(decimal major, string currency = DefaultCurrency) =>
        new((long)Math.Round(major * 100m, MidpointRounding.ToEven), currency);

    public decimal ToDecimal() => Amount / 100m;

    public bool IsZero => Amount == 0;
    public bool IsNegative => Amount < 0;

    private static void AssertSameCurrency(in Money a, in Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.Ordinal))
            throw new DomainException($"Cannot combine {a.Currency} with {b.Currency}.");
    }

    public static Money operator +(Money a, Money b)
    {
        AssertSameCurrency(a, b);
        return new Money(checked(a.Amount + b.Amount), a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        AssertSameCurrency(a, b);
        return new Money(checked(a.Amount - b.Amount), a.Currency);
    }

    public static Money operator *(Money a, int quantity) =>
        new(checked(a.Amount * quantity), a.Currency);

    public static bool operator <(Money a, Money b) { AssertSameCurrency(a, b); return a.Amount < b.Amount; }
    public static bool operator >(Money a, Money b) { AssertSameCurrency(a, b); return a.Amount > b.Amount; }
    public static bool operator <=(Money a, Money b) { AssertSameCurrency(a, b); return a.Amount <= b.Amount; }
    public static bool operator >=(Money a, Money b) { AssertSameCurrency(a, b); return a.Amount >= b.Amount; }

    public int CompareTo(Money other)
    {
        AssertSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>
    /// Splits an amount across n parts without losing or inventing a cent.
    /// The first (remainder) parts each carry one extra minor unit.
    /// </summary>
    public Money[] Allocate(int parts)
    {
        if (parts <= 0) throw new DomainException("Allocation requires at least one part.");

        var basis = Amount / parts;
        var remainder = (int)(Amount - basis * parts);
        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
            result[i] = new Money(basis + (i < Math.Abs(remainder) ? Math.Sign(remainder) : 0), Currency);
        return result;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{ToDecimal():0.00} {Currency}");
}
