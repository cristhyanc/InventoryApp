using Inventory.Application.Time;

namespace Inventory.Application.Stock;

/// <summary>
/// The global, filterable stock-history use case behind the <c>/stock-history</c> page (issue #384).
/// It answers one bounded page of the persisted <c>StockAdjustment</c> history across every product
/// the caller's business owns, so reviewing several products no longer means one history request per
/// product.
///
/// It owns exactly two decisions, and neither is left to the client or to the adapter:
/// <list type="number">
///   <item>the page is bounded by <see cref="StockHistoryPaging"/>; and</item>
///   <item>a requested date range is a pair of business calendar days, converted here
///   to the UTC boundaries of those business days through <see cref="IBusinessCalendar"/> - start
///   inclusive, end exclusive, which stays correct across a daylight-saving transition because the
///   calendar resolves each boundary in its own offset.</item>
/// </list>
///
/// Ordering and filtering are on <c>StockAdjustment.CreatedAt</c>, the same instant the
/// product-specific history has always ordered by (<see cref="GetStockHistory"/> /
/// <c>EfStockAdjustmentStore.ListHistoryAsync</c>), never on <c>EffectiveAt</c>. This is a read: the
/// persisted movements are the source of truth and nothing here recalculates, rewrites or
/// synthesises one. Tenant isolation is the central query filter's job, not this use case's.
/// </summary>
public sealed class ListStockHistory
{
    private readonly IStockAdjustmentStore _store;
    private readonly IBusinessCalendar _calendar;

    public ListStockHistory(IStockAdjustmentStore store, IBusinessCalendar calendar)
    {
        _store = store;
        _calendar = calendar;
    }

    public async Task<StockHistoryPage> Handle(StockHistoryQuery query, CancellationToken cancellationToken)
    {
        var (page, pageSize) = StockHistoryPaging.Resolve(query.Page, query.PageSize);

        var filter = new StockHistoryFilter(
            query.ProductId,
            query.From is { } from ? _calendar.StartOfBusinessDayUtc(from.Date) : null,
            // The day after the requested last business day begins: an exclusive upper boundary
            // includes every movement of the last day whether that day is 23, 24 or 25 hours long.
            query.To is { } to ? _calendar.StartOfBusinessDayUtc(to.Date.AddDays(1)) : null,
            query.Reason,
            query.MachineId,
            query.Source,
            (page - 1) * pageSize,
            pageSize);

        var result = await _store.QueryHistoryAsync(filter, cancellationToken);
        return new StockHistoryPage(result.Entries, result.TotalCount, page, pageSize);
    }
}
