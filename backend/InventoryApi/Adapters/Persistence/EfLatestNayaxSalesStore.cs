using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ILatestNayaxSalesStore"/> (issue #187). It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>, the
/// <see cref="NayaxSales"/> persistence model, and <see cref="NayaxProductMatcher"/>, all of which
/// still live in InventoryApi. Move it into Inventory.Infrastructure once the shared AppDbContext and
/// persistence models relocate there; this follows the same pattern as
/// <see cref="EfMachineStockEventStore"/>.
///
/// The import rules themselves are unchanged from the former private
/// <c>MachineService.SaveMachinesLastSalesAsync</c>: deduplication by the remote
/// <c>TransactionID</c>, product matching through <see cref="NayaxProductMatcher"/>, the
/// settlement-value completed/cancelled default, historical costing through the Application
/// <see cref="ICostSale"/> use case (issue #297), and the baseline-cutoff-gated
/// <see cref="IRebuildProductCost"/> replay. An already stored transaction is only enriched
/// where it is still missing its product match or status, so an imported status or cost is never
/// overwritten.
/// </summary>
public sealed class EfLatestNayaxSalesStore : ILatestNayaxSalesStore
{
    private readonly AppDbContext _db;
    private readonly ICostSale _saleCosting;
    private readonly IRebuildProductCost _inventoryCostRebuild;

    public EfLatestNayaxSalesStore(
        AppDbContext db,
        ICostSale saleCosting,
        IRebuildProductCost inventoryCostRebuild)
    {
        _db = db;
        _saleCosting = saleCosting;
        _inventoryCostRebuild = inventoryCostRebuild;
    }

    public async Task<LatestNayaxSalesPersistResult> PersistLatestSalesAsync(
        IReadOnlyList<NayaxLastSalesReport> sales, CancellationToken cancellationToken)
    {
        var affected = new Dictionary<long, DateTime>();
        if (sales.Count == 0)
            return new LatestNayaxSalesPersistResult(affected);

        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var sale in sales)
        {
            var matchedProduct = NayaxProductMatcher.Match(
                products,
                sale.NayaxProductId,
                sale.ProductName);
            var existing = await _db.NayaxSales
                .FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, cancellationToken);
            if (existing is null)
            {
                var added = new NayaxSales
                {
                    TransactionID = sale.TransactionID,
                    TransactionStatusId = sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250,
                    MachineID = sale.MachineID,
                    NayaxProductId = matchedProduct?.Id ?? sale.NayaxProductId,
                    MachineName = sale.MachineName,
                    SettlementValue = sale.SettlementValue,
                    PaymentMethod = sale.PaymentMethod,
                    ProductName = sale.ProductName,
                    NayaxProductCostPrice = sale.ProductCostPrice,
                    MachineAuthorizationTime = sale.MachineAuthorizationTime
                };
                _db.NayaxSales.Add(added);
                await _saleCosting.CostAsync(added, cancellationToken: cancellationToken);
                if (NayaxTransactionStatusClassifier.IsCompletedSale(added.TransactionStatusId))
                {
                    if (matchedProduct is not null &&
                        (!affected.TryGetValue(matchedProduct.Id, out var existingAt) || added.MachineAuthorizationTime < existingAt))
                        affected[matchedProduct.Id] = added.MachineAuthorizationTime;
                }
                continue;
            }

            var enriched = false;
            if (!existing.NayaxProductId.HasValue)
            {
                var existingMatch = NayaxProductMatcher.Match(
                    products,
                    sale.NayaxProductId,
                    sale.ProductName ?? existing.ProductName);
                if (existingMatch is not null)
                {
                    existing.NayaxProductId = existingMatch.Id;
                    matchedProduct = existingMatch;
                    enriched = true;
                }
            }
            if (!existing.TransactionStatusId.HasValue)
            {
                existing.TransactionStatusId =
                    sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250;
                enriched = true;
            }
            if (enriched)
            {
                await _saleCosting.CostAsync(
                    existing,
                    cancellationToken: cancellationToken);
                if (NayaxTransactionStatusClassifier.IsCompletedSale(existing.TransactionStatusId))
                {
                    matchedProduct ??= NayaxProductMatcher.Match(
                        products, existing.NayaxProductId, existing.ProductName);
                    if (matchedProduct is not null &&
                        (!affected.TryGetValue(matchedProduct.Id, out var existingAt) ||
                         existing.MachineAuthorizationTime < existingAt))
                        affected[matchedProduct.Id] = existing.MachineAuthorizationTime;
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new LatestNayaxSalesPersistResult(affected);
    }

    public async Task RebuildInventoryCostsAsync(
        IReadOnlyDictionary<long, DateTime> earliestCompletedSaleByProductId,
        CancellationToken cancellationToken)
    {
        if (earliestCompletedSaleByProductId.Count == 0)
            return;

        var productIds = earliestCompletedSaleByProductId.Keys.ToList();
        var baselines = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);
        // One product's fatal cost history must not discard the other products' rebuilds: their
        // sales are already saved, so a skipped rebuild would never be retried by a later sync.
        // The failing product stages nothing, the rest are saved, and the failure is still raised.
        var failures = new List<string>();
        foreach (var item in earliestCompletedSaleByProductId)
        {
            if (!baselines.TryGetValue(item.Key, out var cutoff) || item.Value <= cutoff)
                continue;
            try
            {
                await _inventoryCostRebuild.RebuildAsync(item.Key, item.Value, cancellationToken: cancellationToken);
            }
            catch (InventoryCostDataQualityException ex)
            {
                failures.Add(ex.Message);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        if (failures.Count > 0)
            throw new InventoryCostDataQualityException(string.Join(" ", failures));
    }
}
