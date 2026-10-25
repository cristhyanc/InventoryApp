using System.Linq.Expressions;

using Inventory.Domain.Reporting.Dashboard;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The one query behind <see cref="ProductInventoryValuationFact.HasInventoryHistory"/> (issue #861):
/// which of a set of products anything records inventory ever having been in.
///
/// <para>It is a static helper rather than a port because it is not a use case's question - it is how
/// two adapters, <see cref="EfProductCatalogStore"/> and
/// <c>Inventory.Infrastructure.Reporting.Persistence.EfInventoryValuationFactsProvider</c>, answer the
/// same half of the same Domain rule. Writing it once is what stops the Dashboard summary and the
/// dedicated inventory-value endpoint from coming to disagree about whether a product is empty - the
/// same reason <c>EfReportingSharedQueries</c> and <c>EfNayaxSalesQueries</c> exist.</para>
///
/// <para><strong>Tenant scoping is the central <see cref="AppDbContext"/> query filters</strong>, not
/// a predicate here: every table read below is tenant-owned, so another business's movements, sales
/// and counts are invisible and cannot become evidence about this business's product. The product ids
/// are only ever ids the caller already read under those same filters.</para>
/// </summary>
internal static class EfProductInventoryHistoryQueries
{
    /// <summary>
    /// The subset of <paramref name="productIds"/> that any recorded inventory, purchasing, sales or
    /// costing fact refers to.
    ///
    /// <para>The seven kinds are exactly what the Domain fact names: a stock movement (which covers
    /// restocks, corrections, write-offs and refills into a machine, and so every purchase that was
    /// received), a purchase line, a stored sale through the sale's own <c>ProductId</c> (issue #758,
    /// never a provider id or a name), an operator's machine stock count, an inventory-cost
    /// transition baseline (whose opening position includes machine-held stock), a costing repair,
    /// and an imported Nayax machine stock event matched to the product - including one still
    /// unprocessed, because an unresolved machine movement is an unresolved position rather than an
    /// absent one.</para>
    ///
    /// <para>A supplier order line is deliberately absent: an order that has not been received put no
    /// inventory anywhere, and the purchase that receives it is recorded as both a purchase line and a
    /// stock movement. That is the one place this question differs from the deletion rule's reference
    /// counts (issue #736), which protect a row from being erased rather than describe a position.</para>
    ///
    /// <para>One grouped read per kind for the whole set, never per product, and none at all for an
    /// empty set - which is the normal case, because the caller asks only about the products whose
    /// own stored columns could still be empty.</para>
    /// </summary>
    public static async Task<HashSet<long>> WithRecordedInventoryHistoryAsync(
        AppDbContext db, IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        var withHistory = new HashSet<long>();
        if (productIds.Count == 0) return withHistory;

        var ids = productIds.ToList();

        withHistory.UnionWith(await ProductIdsAsync(
            db.StockAdjustments.Where(x => ids.Contains(x.ProductId)), x => x.ProductId));
        withHistory.UnionWith(await ProductIdsAsync(
            db.ReceiptItems.Where(x => ids.Contains(x.ProductId)), x => x.ProductId));
        withHistory.UnionWith(await ProductIdsAsync(
            db.MachineStockCounts.Where(x => ids.Contains(x.ProductId)), x => x.ProductId));
        withHistory.UnionWith(await ProductIdsAsync(
            db.InventoryCostTransitionBaselines.Where(x => ids.Contains(x.ProductId)), x => x.ProductId));
        withHistory.UnionWith(await ProductIdsAsync(
            db.InventoryCostRepairs.Where(x => ids.Contains(x.ProductId)), x => x.ProductId));

        // Both of these name the product through a nullable column - a sale whose product was never
        // resolved belongs to no product at all (issue #758), and a stock event that matched none is
        // about no product either - so the null is excluded before the projection rather than read as
        // a product.
        withHistory.UnionWith(await ProductIdsAsync(
            db.NayaxSales.Where(x => x.ProductId != null && ids.Contains(x.ProductId.Value)),
            x => x.ProductId!.Value));
        withHistory.UnionWith(await ProductIdsAsync(
            db.NayaxMachineStockEvents.Where(
                x => x.MatchedProductId != null && ids.Contains(x.MatchedProductId.Value)),
            x => x.MatchedProductId!.Value));

        return withHistory;

        // The ids rather than the rows: the question is only whether any row exists, so a product's
        // whole history is never materialised to answer it.
        Task<List<long>> ProductIdsAsync<TRow>(
            IQueryable<TRow> rows, Expression<Func<TRow, long>> productId) =>
            rows.Select(productId).Distinct().ToListAsync(cancellationToken);
    }
}
