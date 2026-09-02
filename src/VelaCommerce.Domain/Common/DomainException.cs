namespace VelaCommerce.Domain.Common;

/// <summary>
/// Raised when an operation would break a domain invariant. These are programmer- or
/// caller-errors surfaced as 4xx by the API, never 500s.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
