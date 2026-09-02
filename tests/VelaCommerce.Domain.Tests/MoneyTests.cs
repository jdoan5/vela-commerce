using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// Money underpins every price, total and refund in the system, so these tests pin the two
/// properties a reviewer should be able to trust without reading the implementation:
/// a cent is never created or destroyed by rounding or splitting, and two currencies are
/// never silently coerced into one wrong number.
/// </summary>
public sealed class MoneyTests
{
    public static TheoryData<decimal, long> MajorUnitConversions => new()
    {
        { 19.99m, 1_999L },
        { 0m, 0L },
        { 0.01m, 1L },
        { 1_234.56m, 123_456L },
        { 100m, 10_000L },
        { -19.99m, -1_999L }
    };

    /// <summary>Halfway cases, where naive rounding biases every total upward over time.</summary>
    public static TheoryData<decimal, long> HalfwayCases => new()
    {
        { 0.005m, 0L },
        { 0.015m, 2L },
        { 0.025m, 2L },
        { 0.035m, 4L },
        { -0.015m, -2L }
    };

    [Fact]
    public void An_amount_is_stored_as_the_exact_minor_units_it_was_given()
    {
        var price = new Money(1_999);

        Assert.Equal(1_999L, price.Amount);
        Assert.Equal("USD", price.Currency);
    }

    [Fact]
    public void The_currency_code_is_normalised_to_uppercase_so_usd_and_USD_are_one_currency()
    {
        var lower = new Money(500, "eur");
        var upper = new Money(500, "EUR");

        Assert.Equal("EUR", lower.Currency);
        Assert.Equal(upper, lower);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("US")]
    [InlineData("USDD")]
    public void A_currency_that_is_not_a_three_letter_code_is_rejected(string currency)
    {
        Assert.Throws<DomainException>(() => { _ = new Money(100, currency); });
    }

    [Theory]
    [MemberData(nameof(MajorUnitConversions))]
    public void FromDecimal_converts_major_units_to_minor_units(decimal major, long expectedCents)
    {
        var money = Money.FromDecimal(major);

        Assert.Equal(expectedCents, money.Amount);
    }

    [Theory]
    [MemberData(nameof(HalfwayCases))]
    public void FromDecimal_rounds_halfway_amounts_to_even_so_repeated_conversions_do_not_drift_upward(
        decimal major,
        long expectedCents)
    {
        var money = Money.FromDecimal(major);

        Assert.Equal(expectedCents, money.Amount);
    }

    [Fact]
    public void ToDecimal_round_trips_back_to_the_major_unit_value()
    {
        var money = Money.FromDecimal(19.99m);

        Assert.Equal(19.99m, money.ToDecimal());
    }

    [Fact]
    public void Adding_and_subtracting_operate_on_minor_units()
    {
        var subtotal = new Money(1_999);
        var shipping = new Money(500);

        Assert.Equal(new Money(2_499), subtotal + shipping);
        Assert.Equal(new Money(1_499), subtotal - shipping);
    }

    [Fact]
    public void Subtracting_a_larger_amount_yields_a_negative_amount_rather_than_clamping()
    {
        var refund = new Money(500) - new Money(1_200);

        Assert.Equal(-700L, refund.Amount);
        Assert.True(refund.IsNegative);
    }

    [Theory]
    [InlineData(1_999L, 3, 5_997L)]
    [InlineData(1_999L, 1, 1_999L)]
    [InlineData(1_999L, 0, 0L)]
    [InlineData(250L, 99, 24_750L)]
    public void Multiplying_by_a_quantity_scales_the_minor_units(long unitPrice, int quantity, long expected)
    {
        var lineTotal = new Money(unitPrice) * quantity;

        Assert.Equal(expected, lineTotal.Amount);
        Assert.Equal("USD", lineTotal.Currency);
    }

    [Fact]
    public void Adding_two_different_currencies_throws_instead_of_producing_a_meaningless_total()
    {
        var usd = new Money(1_000, "USD");
        var eur = new Money(1_000, "EUR");

        var ex = Assert.Throws<DomainException>(() => { _ = usd + eur; });

        Assert.Contains("USD", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EUR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Subtracting_two_different_currencies_throws()
    {
        var usd = new Money(1_000, "USD");
        var gbp = new Money(1_000, "GBP");

        Assert.Throws<DomainException>(() => { _ = usd - gbp; });
    }

    [Fact]
    public void Comparing_two_different_currencies_throws_because_the_ordering_would_be_a_lie()
    {
        var usd = new Money(1_000, "USD");
        var jpy = new Money(1_000, "JPY");

        Assert.Throws<DomainException>(() => { _ = usd < jpy; });
        Assert.Throws<DomainException>(() => { _ = usd.CompareTo(jpy); });
    }

    [Fact]
    public void Comparison_operators_order_amounts_by_their_minor_units()
    {
        var cheaper = new Money(1_000);
        var dearer = new Money(2_500);

        Assert.True(cheaper < dearer);
        Assert.True(dearer > cheaper);
        Assert.True(cheaper <= new Money(1_000));
        Assert.True(cheaper >= new Money(1_000));
    }

    [Fact]
    public void CompareTo_reports_the_ordering_so_amounts_can_be_sorted()
    {
        var cheaper = new Money(1_000);
        var dearer = new Money(2_500);

        Assert.True(cheaper.CompareTo(dearer) < 0);
        Assert.True(dearer.CompareTo(cheaper) > 0);
        Assert.Equal(0, cheaper.CompareTo(new Money(1_000)));
    }

    [Fact]
    public void Zero_is_zero_in_the_currency_that_was_asked_for()
    {
        var zero = Money.Zero("GBP");

        Assert.True(zero.IsZero);
        Assert.False(zero.IsNegative);
        Assert.Equal("GBP", zero.Currency);
    }

    [Theory]
    [InlineData(100L, 3)]
    [InlineData(10L, 3)]
    [InlineData(1L, 3)]
    [InlineData(0L, 4)]
    [InlineData(5L, 5)]
    [InlineData(7L, 1)]
    [InlineData(999L, 7)]
    [InlineData(-10L, 3)]
    [InlineData(-100L, 3)]
    public void Allocating_across_parts_never_loses_or_invents_a_cent(long amount, int parts)
    {
        var slices = new Money(amount).Allocate(parts);

        Assert.Equal(parts, slices.Length);
        Assert.Equal(amount, slices.Sum(slice => slice.Amount));
    }

    [Fact]
    public void The_remainder_cents_go_to_the_earliest_parts_so_the_split_is_deterministic()
    {
        var slices = new Money(10).Allocate(3);

        Assert.Equal(new[] { 4L, 3L, 3L }, slices.Select(slice => slice.Amount));
    }

    [Fact]
    public void Allocating_a_negative_amount_splits_the_deficit_the_same_way()
    {
        var slices = new Money(-10).Allocate(3);

        Assert.Equal(new[] { -4L, -3L, -3L }, slices.Select(slice => slice.Amount));
        Assert.Equal(-10L, slices.Sum(slice => slice.Amount));
    }

    [Fact]
    public void Allocated_parts_keep_the_currency_of_the_original_amount()
    {
        var slices = new Money(100, "EUR").Allocate(3);

        Assert.All(slices, slice => Assert.Equal("EUR", slice.Currency));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Allocating_across_fewer_than_one_part_is_rejected(int parts)
    {
        Assert.Throws<DomainException>(() => { _ = new Money(100).Allocate(parts); });
    }
}
