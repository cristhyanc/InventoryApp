using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductPurchaseCostFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason the reporting
/// <c>Ef&lt;Feature&gt;ReportFactsProvider</c> adapters do: it depends on <see cref="AppDbContext"/>,
/// which moved there in issue #307 ahead of this adapter family (issue #309, Persistence 8/8 of
/// #153). The context's global business query filter already scopes this
/// projection to the caller's business, so no per-call filter is needed here.
///
/// Fetches every requested product's actual <c>ReceiptItems</c> (PurchaseItem) rows in one query
/// (issue #207), the same projection <see cref="EfProductPurchasePriceHistoryProvider"/> uses for one
/// product at a time, so the product profitability report never issues a per-product history query.
/// </summary>
public sealed class EfProductPurchaseCostFactsProvider : IProductPurchaseCostFactsProvider
{
    private readonly AppDbContext _db;

    public EfProductPurchaseCostFactsProvider(AppDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>> GetForProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>();

        return await GroupedHistoryAsync(
            _db.ReceiptItems.Where(item => productIds.Contains(item.ProductId)), cancellationToken);
    }

    /// <summary>
    /// The same projection over the whole business's purchase history (issue #788). The query is the
    /// one above without the id predicate, so the two reads cannot project different facts; the
    /// context's global business query filter is what scopes it, exactly as it scopes the other.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>> GetForAllProductsAsync(
        CancellationToken cancellationToken) =>
        await GroupedHistoryAsync(_db.ReceiptItems, cancellationToken);

    private static async Task<IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>> GroupedHistoryAsync(
        IQueryable<PurchaseItem> items, CancellationToken cancellationToken)
    {
        var rows = await items
            .AsNoTracking()
            .Select(item => new
            {
                item.ProductId,
                item.Id,
                item.ReceiptId,
                PurchaseTitle = item.Purchase!.Title,
                item.Purchase!.PurchaseDate,
                item.Purchase!.SupplierId,
                SupplierName = item.Purchase!.Supplier != null ? item.Purchase!.Supplier.Name : null,
                item.UnitCost
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SupplierPriceHistoryEntry>)group
                    .Select(row => new SupplierPriceHistoryEntry(
                        row.Id, row.ReceiptId, row.PurchaseTitle, row.PurchaseDate, row.SupplierId, row.SupplierName, row.UnitCost))
                    .ToList());
    }
}
