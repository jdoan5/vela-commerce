namespace VelaCommerce.Api.Contracts;

/// <summary>
/// A category facet with its live product count.
/// <para>
/// The count is returned with the name because a facet list without counts forces the client
/// into one extra request per facet just to know which filters lead somewhere.
/// </para>
/// </summary>
public sealed record CategoryResponse(string Name, int ProductCount);
