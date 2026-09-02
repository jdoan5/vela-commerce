namespace VelaCommerce.Domain.Common;

/// <summary>
/// A postal address captured at checkout. Persisted as jsonb rather than its own table:
/// it is immutable once the order is placed and is never queried across orders.
/// </summary>
public sealed record ShippingAddress
{
    public required string Recipient { get; init; }
    public required string Line1 { get; init; }
    public string? Line2 { get; init; }
    public required string City { get; init; }
    public string? Region { get; init; }
    public required string PostalCode { get; init; }
    /// <summary>ISO 3166-1 alpha-2, uppercase.</summary>
    public required string CountryCode { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Recipient)) throw new DomainException("Recipient is required.");
        if (string.IsNullOrWhiteSpace(Line1)) throw new DomainException("Address line 1 is required.");
        if (string.IsNullOrWhiteSpace(City)) throw new DomainException("City is required.");
        if (string.IsNullOrWhiteSpace(PostalCode)) throw new DomainException("Postal code is required.");
        if (CountryCode is not { Length: 2 }) throw new DomainException("Country must be an ISO alpha-2 code.");
    }
}
