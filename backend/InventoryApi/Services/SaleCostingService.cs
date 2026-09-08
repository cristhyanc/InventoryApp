using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed class SaleCostingService : ISaleCostingService
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostRebuildService _rebuild;

    public SaleCostingService(AppDbContext db, IInventoryCostRebuildService? rebuild = null)
    {
        _db = db;
        _rebuild = rebuild ?? new InventoryCostRebuildService(db);
    }

    public async Task CostSaleAsync(NayaxSales sale, bool allowLegacyEstimate = false, CancellationToken cancellationToken = default)
    {
        if (!NayaxTransactionStatusClassifier.IsCompletedSale(sale))
        {
            sale.UnitCostAtSale = null;
            sale.CostOfGoodsSold = null;
            sale.CostingStatus = SaleCostingStatus.Pending;
            return;
        }

        var product = NayaxProductMatcher.Match(
            await _db.Products.ToListAsync(cancellationToken),
            sale.NayaxProductId,
            sale.ProductName);

        if (product is null)
        {
            sale.UnitCostAtSale = null;
            sale.CostOfGoodsSold = null;
            sale.CostingStatus = SaleCostingStatus.Error;
            return;
        }

        var cost = await GetAverageUnitCostAtAsync(product.Id, sale.MachineAuthorizationTime, sale.TransactionID, cancellationToken);
        if (cost is > 0)
        {
            sale.UnitCostAtSale = cost.Value;
            sale.CostOfGoodsSold = cost.Value;
            sale.CostingStatus = SaleCostingStatus.Costed;
        }
        else
        {
            sale.UnitCostAtSale = null;
            sale.CostOfGoodsSold = null;
            sale.CostingStatus = SaleCostingStatus.Pending;
        }
    }

    public async Task<int> CostPendingSalesAsync(long? productId = null, bool allowLegacyEstimate = false, CancellationToken cancellationToken = default)
    {
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var sales = (await _db.NayaxSales
            .Where(s => s.CostingStatus == SaleCostingStatus.Pending &&
                        s.TransactionStatusId == NayaxTransactionStatusIds.Completed)
            .OrderBy(s => s.MachineAuthorizationTime)
            .ToListAsync(cancellationToken))
            .Where(s => !productId.HasValue ||
                NayaxProductMatcher.Match(products, s.NayaxProductId, s.ProductName)?.Id == productId.Value)
            .ToList();

        foreach (var sale in sales)
            await CostSaleAsync(sale, allowLegacyEstimate, cancellationToken);

        if (sales.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return sales.Count(s => s.CostingStatus == SaleCostingStatus.Costed);
    }

    public async Task<SaleCostingBackfillResult> BackfillAsync(bool dryRun = true, bool force = false, CancellationToken cancellationToken = default)
    {
        var sales = await _db.NayaxSales
            .Where(s => s.TransactionStatusId == NayaxTransactionStatusIds.Completed)
            .OrderBy(s => s.MachineAuthorizationTime)
            .ToListAsync(cancellationToken);
        var costed = 0;
        var estimated = 0;
        var pending = 0;
        var errors = 0;
        var finalized = 0;

        foreach (var sale in sales)
        {
            if (!force && (sale.CostingStatus == SaleCostingStatus.Costed ||
                           sale.CostingStatus == SaleCostingStatus.LegacyEstimated))
            {
                finalized++;
                continue;
            }

            await CostSaleAsync(sale, allowLegacyEstimate: false, cancellationToken);
            switch (sale.CostingStatus)
            {
                case SaleCostingStatus.Costed: costed++; break;
                case SaleCostingStatus.LegacyEstimated: estimated++; break;
                case SaleCostingStatus.Pending: pending++; break;
                case SaleCostingStatus.Error: errors++; break;
            }
        }

        if (!dryRun)
            await _db.SaveChangesAsync(cancellationToken);
        else
            _db.ChangeTracker.Clear();

        return new SaleCostingBackfillResult(costed, estimated, pending, errors, finalized, dryRun);
    }

    public Task<decimal?> GetAverageUnitCostAtAsync(long productId, DateTime saleTime, CancellationToken cancellationToken = default) =>
        _rebuild.GetAverageUnitCostAtAsync(productId, saleTime, cancellationToken: cancellationToken);

    private Task<decimal?> GetAverageUnitCostAtAsync(
        long productId,
        DateTime saleTime,
        long saleTransactionId,
        CancellationToken cancellationToken) =>
        _rebuild.GetAverageUnitCostAtAsync(productId, saleTime, saleTransactionId, cancellationToken);
}
