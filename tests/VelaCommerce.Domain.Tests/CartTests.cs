using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Tests;

/// <summary>
/// The cart is the aggregate shoppers poke at hardest, so the rules pinned here are the ones
/// that show up as visible bugs when they break: the same variant added twice must be one
/// line rather than two, quantities stay inside the demo's cap, and a cart never mixes
/// currencies because its subtotal would then be unaddable.
/// </summary>
public sealed class CartTests
{
    private static Cart NewCart(string currency = Money.DefaultCurrency) =>
        new(Guid.CreateVersion7(), currency);

    [Fact]
    public void A_new_cart_is_empty_and_its_subtotal_is_zero_in_the_cart_currency()
    {
        var cart = NewCart("EUR");

        Assert.True(cart.IsEmpty);
        Assert.Equal(0, cart.TotalQuantity);
        Assert.Equal(Money.Zero("EUR"), cart.Subtotal);
    }

    [Fact]
    public void The_cart_currency_is_normalised_to_uppercase()
    {
        var cart = NewCart("gbp");

        Assert.Equal("GBP", cart.Currency);
    }

    [Fact]
    public void Adding_the_same_variant_twice_merges_into_one_line_instead_of_duplicating_it()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();

        cart.AddItem(variantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);
        cart.AddItem(variantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 3);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(5, cart.TotalQuantity);
    }

    [Fact]
    public void Adding_different_variants_creates_a_line_each()
    {
        var cart = NewCart();

        cart.AddItem(Guid.CreateVersion7(), "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 1);
        cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200), 1);

        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public void A_line_captures_the_price_it_was_added_at_and_multiplies_it_by_the_quantity()
    {
        var cart = NewCart();

        var line = cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200), 3);

        Assert.Equal(new Money(1_200), line.UnitPrice);
        Assert.Equal(new Money(3_600), line.LineTotal);
    }

    [Fact]
    public void The_subtotal_sums_the_line_totals_across_mixed_lines()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);
        cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200), 3);

        Assert.Equal(new Money(12_600), cart.Subtotal);
        Assert.Equal(5, cart.TotalQuantity);
    }

    [Fact]
    public void Changing_a_quantity_to_zero_removes_the_line_rather_than_leaving_an_empty_one()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);

        cart.ChangeQuantity(variantId, 0);

        Assert.True(cart.IsEmpty);
        Assert.Equal(Money.Zero(), cart.Subtotal);
    }

    [Fact]
    public void Changing_a_quantity_replaces_it_rather_than_adding_to_it()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);

        cart.ChangeQuantity(variantId, 5);

        Assert.Equal(5, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public void Changing_the_quantity_of_a_variant_that_is_not_in_the_cart_throws()
    {
        var cart = NewCart();

        Assert.Throws<DomainException>(() => cart.ChangeQuantity(Guid.CreateVersion7(), 1));
    }

    [Fact]
    public void A_line_quantity_above_ninety_nine_is_refused_by_the_demo_cap()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 1);

        Assert.Throws<DomainException>(() => cart.ChangeQuantity(variantId, 100));
        Assert.Equal(1, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public void The_demo_cap_also_applies_when_the_line_is_created_not_just_changed()
    {
        // Regression: the cap originally lived only in ChangeQuantity, so adding a
        // brand-new line of 500 silently succeeded and bypassed it entirely.
        var cart = NewCart();

        Assert.Throws<DomainException>(() =>
            cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200), 500));

        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Merging_into_an_existing_line_cannot_be_used_to_climb_past_the_cap()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 60);

        Assert.Throws<DomainException>(() =>
            cart.AddItem(variantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 60));

        Assert.Equal(60, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public void Ninety_nine_is_still_allowed_because_the_cap_is_inclusive()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 1);

        cart.ChangeQuantity(variantId, 99);

        Assert.Equal(99, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public void Merging_an_add_that_would_push_a_line_past_the_cap_is_refused()
    {
        var cart = NewCart();
        var variantId = Guid.CreateVersion7();
        cart.AddItem(variantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 95);

        Assert.Throws<DomainException>(() =>
        {
            _ = cart.AddItem(variantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 10);
        });
    }

    [Fact]
    public void An_item_priced_in_another_currency_cannot_join_the_cart()
    {
        var cart = NewCart("USD");

        var ex = Assert.Throws<DomainException>(() =>
        {
            _ = cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200, "EUR"), 1);
        });

        Assert.Contains("USD", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EUR", ex.Message, StringComparison.Ordinal);
        Assert.True(cart.IsEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Adding_a_non_positive_quantity_throws(int quantity)
    {
        var cart = NewCart();

        Assert.Throws<DomainException>(() =>
        {
            _ = cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200), quantity);
        });
    }

    [Fact]
    public void Removing_an_item_drops_its_line()
    {
        var cart = NewCart();
        var keptVariantId = Guid.CreateVersion7();
        var removedVariantId = Guid.CreateVersion7();
        cart.AddItem(keptVariantId, "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 1);
        cart.AddItem(removedVariantId, "VELA-MUG-02", "Ridge Mug", new Money(1_200), 1);

        cart.RemoveItem(removedVariantId);

        Assert.Equal(keptVariantId, Assert.Single(cart.Lines).VariantId);
        Assert.Equal(new Money(4_500), cart.Subtotal);
    }

    [Fact]
    public void Clearing_the_cart_empties_it()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 2);
        cart.AddItem(Guid.CreateVersion7(), "VELA-MUG-02", "Ridge Mug", new Money(1_200), 3);

        cart.Clear();

        Assert.True(cart.IsEmpty);
        Assert.Equal(0, cart.TotalQuantity);
        Assert.Equal(Money.Zero(), cart.Subtotal);
    }

    [Fact]
    public void Every_line_belongs_to_the_cart_that_created_it()
    {
        var cart = NewCart();

        var line = cart.AddItem(Guid.CreateVersion7(), "VELA-TOTE-01", "Harbour Tote", new Money(4_500), 1);

        Assert.Equal(cart.Id, line.CartId);
    }
}
