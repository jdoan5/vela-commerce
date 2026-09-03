namespace VelaCommerce.Storefront.Checkout;

/// <summary>
/// The address form's state, and the client half of a rule the server owns.
///
/// <para>
/// <strong>The validation here is a mirror, not an authority.</strong> Every message it produces is
/// copied word for word from <c>ShippingAddress.Validate()</c> in the domain, and every rule is
/// exactly as strict — no stricter. That last part is the discipline that matters: a client rule the
/// server does not have refuses an address the shop would happily accept, and the shopper has no way
/// to find out that the refusal is imaginary. So the country check is "two characters after
/// trimming", which is what the domain checks, and not "two letters from the ISO list", which it
/// does not.
/// </para>
/// <para>
/// The point of mirroring at all is latency and manners: a missing city should be pointed at while
/// the shopper is still looking at the field, not after a round trip to an API that may be asleep.
/// When the two disagree the server wins and its ProblemDetails is what goes on screen.
/// </para>
/// </summary>
public sealed class AddressDraft
{
    /// <summary>Who to hand the parcel to. Required.</summary>
    public string Recipient { get; set; } = "";

    /// <summary>Street address. Required.</summary>
    public string Line1 { get; set; } = "";

    /// <summary>Apartment, unit, care-of. Optional, and sent as null when blank so the order does not store an empty string.</summary>
    public string Line2 { get; set; } = "";

    /// <summary>Required.</summary>
    public string City { get; set; } = "";

    /// <summary>State, province or county. Optional, because most of the world does not use one.</summary>
    public string Region { get; set; } = "";

    /// <summary>Required.</summary>
    public string PostalCode { get; set; } = "";

    /// <summary>ISO 3166-1 alpha-2. Upper-cased on the way out, so "us" is accepted exactly as the API accepts it.</summary>
    public string CountryCode { get; set; } = "";

    /// <summary>True when nothing has been typed yet, which is what the page uses to decide whether offering a sample address is still useful.</summary>
    public bool IsUntouched =>
        string.IsNullOrWhiteSpace(Recipient)
        && string.IsNullOrWhiteSpace(Line1)
        && string.IsNullOrWhiteSpace(Line2)
        && string.IsNullOrWhiteSpace(City)
        && string.IsNullOrWhiteSpace(Region)
        && string.IsNullOrWhiteSpace(PostalCode)
        && string.IsNullOrWhiteSpace(CountryCode);

    /// <summary>
    /// Fills the form with an address that passes. This is a demo shop and the fastest honest route
    /// to a placed order is a button that says what it does; the alternative — silently pre-filling
    /// the fields — hides the fact that the form validates at all.
    /// </summary>
    public void FillWithSample()
    {
        Recipient = "Marta Ellery";
        Line1 = "14 Harbour Reach";
        Line2 = "Berth 7";
        City = "Portsmouth";
        Region = "NH";
        PostalCode = "03801";
        CountryCode = "US";
    }

    /// <summary>
    /// The domain's rules, checked in the domain's own words.
    /// </summary>
    /// <returns>
    /// Field name to message, empty when the address would be accepted. Keyed by the property name
    /// so the page can put each message beside the input it belongs to rather than piling them into
    /// one summary a screen reader has to be pointed at separately.
    /// </returns>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(Recipient))
            errors[nameof(Recipient)] = "Recipient is required.";

        if (string.IsNullOrWhiteSpace(Line1))
            errors[nameof(Line1)] = "Address line 1 is required.";

        if (string.IsNullOrWhiteSpace(City))
            errors[nameof(City)] = "City is required.";

        if (string.IsNullOrWhiteSpace(PostalCode))
            errors[nameof(PostalCode)] = "Postal code is required.";

        // Length after trimming, matching the server's ToDomainAddress → Validate() sequence
        // exactly. Two spaces would pass a naive Length check on the raw string and then fail on the
        // server, which is the precise kind of disagreement this method exists to avoid.
        if (CountryCode.Trim().Length != 2)
            errors[nameof(CountryCode)] = "Country must be an ISO alpha-2 code.";

        return errors;
    }

    /// <summary>
    /// The wire shape, trimmed and cased the same way the server would trim and case it.
    /// <para>
    /// Doing it here rather than leaving it to the API is not redundancy: the address the shopper is
    /// shown on the confirmation page is the one that was stored, so normalising before sending
    /// means what they typed and what they are shown cannot differ by a trailing space.
    /// </para>
    /// </summary>
    public CheckoutAddressBody ToBody() => new(
        Recipient.Trim(),
        Line1.Trim(),
        NullIfBlank(Line2),
        City.Trim(),
        NullIfBlank(Region),
        PostalCode.Trim(),
        CountryCode.Trim().ToUpperInvariant());

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
