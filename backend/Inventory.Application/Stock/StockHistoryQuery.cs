using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// What the global Stock History page asks for (issue #384): movements across every product the
/// caller's business owns, optionally narrowed, and always one bounded page at a time.
///
/// <para>The date range is a pair of <c>Australia/Sydney</c> calendar days, not instants.
/// <see cref="ListStockHistory"/> converts them to the UTC boundaries of those business days (start
/// inclusive, end exclusive) through <see cref="Time.IBusinessCalendar"/>, so a client never has to
/// know the business timezone or its daylight-saving transitions. Any time component is ignored.</para>
///
/// <para><see cref="Page"/>/<see cref="PageSize"/> are what the client asked for;
/// <see cref="StockHistoryPaging"/> decides what it gets.</para>
/// </summary>
public sealed record StockHistoryQuery(
    long? ProductId = null,
    DateTime? From = null,
    DateTime? To = null,
    StockAdjustmentReason? Reason = null,
    long? MachineId = null,
    StockAdjustmentSource? Source = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// The resolved query the persistence port is given: business-day dates have become UTC instants
/// over <c>StockAdjustment.CreatedAt</c> and the requested page has become a bounded
/// <see cref="Skip"/>/<see cref="Take"/>. The store applies these predicates and nothing else - it
/// never decides a window or a bound, and it never adds a business predicate, because tenant
/// ownership is enforced centrally (AGENTS.md § Tenant ownership and data isolation).
///
/// <para><c>CreatedFromUtc</c> is an inclusive lower boundary on <c>CreatedAt</c> and
/// <c>CreatedBeforeUtc</c> an exclusive upper one; <c>null</c> leaves that side open.</para>
/// </summary>
public sealed record StockHistoryFilter(
    long? ProductId,
    DateTime? CreatedFromUtc,
    DateTime? CreatedBeforeUtc,
    StockAdjustmentReason? Reason,
    long? MachineId,
    StockAdjustmentSource? Source,
    int Skip,
    int Take);

/// <summary>
/// One persisted movement as the global history reads it: the movement exactly as
/// <see cref="StockAdjustmentRecord"/> describes it, plus the name of the product it belongs to, so
/// a cross-product listing can label its rows without a request per product.
/// </summary>
public sealed record StockHistoryEntry(StockAdjustmentRecord Movement, string ProductName);

/// <summary>
/// What the store answers: the rows of the requested slice, and how many movements the filter
/// matches in total (not how many were returned).
/// </summary>
public sealed record StockHistoryResult(IReadOnlyList<StockHistoryEntry> Entries, int TotalCount);

/// <summary>
/// One bounded page of the global history. <see cref="Page"/>/<see cref="PageSize"/> are the
/// resolved values the caller actually received, which may differ from what it requested.
/// </summary>
public sealed record StockHistoryPage(
    IReadOnlyList<StockHistoryEntry> Entries,
    int TotalCount,
    int Page,
    int PageSize)
{
    /// <summary>Whether movements matching the filter remain after this page.</summary>
    public bool HasMore => (long)Page * PageSize < TotalCount;
}
