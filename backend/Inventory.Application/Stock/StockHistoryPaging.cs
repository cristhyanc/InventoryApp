namespace Inventory.Application.Stock;

/// <summary>
/// The server-enforced bound on a global stock-history page (issue #384). A stock-adjustment history
/// grows without limit, so the size of a page is the server's decision, not the client's: a missing,
/// zero or negative page size becomes <see cref="DefaultPageSize"/> and anything above
/// <see cref="MaxPageSize"/> is clamped to it, rather than refused - a bounded answer is more useful
/// to the page than an error, and the resolved size is reported back in the response.
/// </summary>
public static class StockHistoryPaging
{
    /// <summary>The page size used when the client does not ask for one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page the server will serve, whatever the client asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// The page number and page size the server will actually serve for a requested pair.
    /// </summary>
    public static (int Page, int PageSize) Resolve(int? page, int? pageSize) =>
        (page is > 0 ? page.Value : 1,
            pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize);
}
