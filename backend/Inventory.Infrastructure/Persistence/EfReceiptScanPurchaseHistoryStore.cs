using Inventory.Application.ReceiptScanning;
using Inventory.Domain.Purchases;
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
/// <para><strong>"Latest" is not decided here.</strong> One bounded query reads the matched history -
/// the requested products, and only the purchases that recorded this supplier - and
/// <see cref="SupplierPurchaseRecency"/>, the one ordering rule
/// <c>SupplierPriceComparisonPolicy</c> (issue #63) orders its own history with, then names each
/// product's latest line: the purchase date descending, then the purchase-item id descending, so a
/// same-day tie goes to the same line both paths would choose. The ordering is not restated here, so
/// revising that tie-break on its own issue cannot leave this adapter behind (AGENTS.md
/// § Architecture rules). Four columns leave the database - the product, the purchase-item id, the
/// unit cost and the purchase date - in one statement for the whole receipt, never one query per
/// product.</para>
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

        // One statement for the whole receipt: the requested products' lines from this supplier's
        // purchases, which is bounded by the receipt's own lines and makes no per-product query.
        var rows = await _db.ReceiptItems
            .AsNoTracking()
            .Where(item =>
                ids.Contains(item.ProductId)
                && item.Purchase!.SupplierId == supplierId)
            .Select(item => new
            {
                item.ProductId,
                item.Id,
                item.UnitCost,
                item.Purchase!.PurchaseDate,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Which line is the latest is SupplierPurchaseRecency's answer, not this adapter's. The
        // purchase date is stored as an instant and is published as the calendar date it records; the
        // conversion is here rather than in the query because it is not something to translate.
        return rows
            .GroupBy(row => row.ProductId)
            .Select(perProduct => SupplierPurchaseRecency
                .NewestFirst(perProduct, row => row.PurchaseDate, row => row.Id)
                .First())
            .Select(latest => new ReceiptScanLastPurchase(
                latest.ProductId, latest.UnitCost, DateOnly.FromDateTime(latest.PurchaseDate)))
            .ToList();
    }
}
