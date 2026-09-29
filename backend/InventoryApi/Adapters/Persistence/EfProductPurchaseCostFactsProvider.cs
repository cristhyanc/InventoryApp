using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Domain.Purchases;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IProductPurchaseCostFactsProvider"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, for the same reason the other
/// <c>Ef&lt;Feature&gt;ReportFactsProvider</c> adapters do: it depends on <see cref="AppDbContext"/>,
/// which still lives in InventoryApi. The context's global business query filter already scopes this
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

        var rows = await _db.ReceiptItems
            .AsNoTracking()
            .Where(item => productIds.Contains(item.ProductId))
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
