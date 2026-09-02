namespace VelaCommerce.Api.Contracts;

/// <summary>
/// The one paging envelope every list endpoint returns.
/// <para>
/// It exists so a client never has to guess whether a short page means "end of results" or
/// "the server clamped my page size" — <see cref="PageSize"/> is echoed back as the value the
/// server actually applied, which is not necessarily the one that was asked for.
/// </para>
/// </summary>
/// <param name="Items">The page of results, in the order the server applied.</param>
/// <param name="Page">1-based page index actually served.</param>
/// <param name="PageSize">Page size actually applied after clamping.</param>
/// <param name="Total">Total matching rows across all pages, before paging.</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total)
{
    /// <summary>Derived rather than passed in, so it can never disagree with Total and PageSize.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}
