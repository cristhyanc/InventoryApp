using Inventory.Application.Purchases;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IProductPurchasePriceHistoryProvider"/>. It still
/// lives in InventoryApi, not Inventory.Infrastructure, for the same reason the other
/// <c>Ef&lt;Feature&gt;ReportFactsProvider</c> adapters do: it depends on <see cref="AppDbContext"/>,
/// which moved to Inventory.Infrastructure in issue #307 ahead of this adapter family
/// (Persistence 7/8 and 8/8 of #153). The context's global business query filter already scopes this
/// projection to the caller's business, so no per-call filter is needed here. Derives history from
/// actual <c>ReceiptItems</c> (PurchaseItem) rows joined to their owning Purchase/Supplier rather than
/// a separate quoted-price table, per issue #63.
/// </summary>
public sealed class EfProductPurchasePriceHistoryProvider : IProductPurchasePriceHistoryProvider
{
    private readonly AppDbContext _db;

    public EfProductPurchasePriceHistoryProvider(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<SupplierPriceHistoryFact>> GetForProductAsync(long productId, CancellationToken cancellationToken) =>
        await _db.ReceiptItems
            .AsNoTracking()
            .Where(item => item.ProductId == productId)
            .Select(item => new SupplierPriceHistoryFact(
                item.Id,
                item.ReceiptId,
                item.Purchase!.Title,
                item.Purchase!.PurchaseDate,
                item.Purchase!.SupplierId,
                item.Purchase!.Supplier != null ? item.Purchase!.Supplier.Name : null,
                item.UnitCost))
            .ToListAsync(cancellationToken);
}
