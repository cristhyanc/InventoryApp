using Inventory.Application.ReceiptScanning;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IReceiptScanPurchaseHistoryStore"/> (issue #1165,
/// sub-issue E of #975): the latest recorded purchase line for each named product from one supplier,
/// so a receipt that printed no pack size can be offered the one the business last paid for.
///
/// <para><strong>The business is the request's own, through the ordinary tenant query filters.</strong>
/// The method takes no business id, no predicate, no column list, no page and no sort, and there is no
/// <c>IgnoreQueryFilters</c> here - so only this business's purchases are read, and nothing else can
/// be asked for (AGENTS.md § Tenant ownership and data isolation). It is <c>AsNoTracking</c> and
/// read-only: no <c>SaveChanges</c>, no transaction, no lease.</para>
///
/// <para><strong>"Latest" is one row per product, decided in the database.</strong> The predicate
/// keeps the line no later line of the same product and supplier supersedes - a later purchase date,
/// or the same date with a higher purchase-item id - which is <c>SupplierPriceComparisonPolicy</c>'s
/// own newest-first tie-break (issue #63) and is strict, so exactly one line per product survives it
/// however many lines share a date. Four columns leave the database: the product, the unit cost, the
/// purchase date and nothing else.</para>
/// </summary>
public sealed class EfReceiptScanPurchaseHistoryStore : IReceiptScanPurchaseHistoryStore
{
    private readonly AppDbContext _db;

    public EfReceiptScanPurchaseHistoryStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceiptScanLastPurchase>> ListLatestAsync(
        int supplierId,
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
        {
            return [];
        }

        var ids = productIds.ToList();

        var rows = await _db.ReceiptItems
            .AsNoTracking()
            .Where(item =>
                ids.Contains(item.ProductId)
                && item.Purchase!.SupplierId == supplierId
                && !_db.ReceiptItems.Any(later =>
                    later.ProductId == item.ProductId
                    && later.Purchase!.SupplierId == supplierId
                    && (later.Purchase.PurchaseDate > item.Purchase.PurchaseDate
                        || (later.Purchase.PurchaseDate == item.Purchase.PurchaseDate
                            && later.Id > item.Id))))
            .Select(item => new
            {
                item.ProductId,
                item.UnitCost,
                item.Purchase!.PurchaseDate,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The purchase date is stored as an instant and is published as the calendar date it records;
        // the conversion is here rather than in the query because it is not something to translate.
        return rows
            .Select(row => new ReceiptScanLastPurchase(
                row.ProductId, row.UnitCost, DateOnly.FromDateTime(row.PurchaseDate)))
            .ToList();
    }
}
