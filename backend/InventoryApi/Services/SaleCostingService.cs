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

    public async Task CostSaleAsync(
        NayaxSales sale,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!NayaxTransactionStatusClassifier.IsCompletedSale(sale))
        {
            sale.UnitCostAtSale = null;
            sale.CostOfGoodsSold = null;
            sale.CostingStatus = SaleCostingStatus.Pending;
            sale.CostSource = SaleCostSource.Unknown;
            return;
        }

        if (!force &&
            sale.CostingStatus is SaleCostingStatus.Costed or SaleCostingStatus.LegacyEstimated &&
            sale.UnitCostAtSale.HasValue && sale.CostOfGoodsSold.HasValue)
            return;

        var product = NayaxProductMatcher.Match(
            await _db.Products.ToListAsync(cancellationToken),
            sale.NayaxProductId,
            sale.ProductName);

        var baselineCutoff = product is null
            ? null
            : await _db.InventoryCostTransitionBaselines.AsNoTracking()
                .Where(x => x.ProductId == product.Id)
                .Select(x => (DateTime?)x.CutoffAt)
                .SingleOrDefaultAsync(cancellationToken);
        var cost = product is null || baselineCutoff.HasValue && sale.MachineAuthorizationTime <= baselineCutoff.Value
            ? null
            : await GetAverageUnitCostAtAsync(product.Id, sale.MachineAuthorizationTime, sale.TransactionID, cancellationToken);
        if (cost is >= 0)
        {
            sale.UnitCostAtSale = cost.Value;
            sale.CostOfGoodsSold = cost.Value;
            sale.CostingStatus = SaleCostingStatus.Costed;
            sale.CostSource = SaleCostSource.InventoryLedger;
        }
        else if (sale.NayaxProductCostPrice is >= 0)
        {
            sale.UnitCostAtSale = sale.NayaxProductCostPrice.Value;
            sale.CostOfGoodsSold = sale.NayaxProductCostPrice.Value;
            sale.CostingStatus = SaleCostingStatus.Costed;
            sale.CostSource = SaleCostSource.NayaxTransactionExport;
        }
        else
        {
            sale.UnitCostAtSale = null;
            sale.CostOfGoodsSold = null;
            sale.CostingStatus = product is null || sale.NayaxProductCostPrice < 0
                ? SaleCostingStatus.Error
                : SaleCostingStatus.Pending;
            sale.CostSource = SaleCostSource.Unknown;
        }
    }

    public async Task<int> CostPendingSalesAsync(long? productId = null, CancellationToken cancellationToken = default)
    {
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var sales = (await _db.NayaxSales
            .Where(s => s.CostingStatus == SaleCostingStatus.Pending)
            .Where(NayaxTransactionStatusClassifier.CompletedSalePredicate)
            .OrderBy(s => s.MachineAuthorizationTime)
            .ToListAsync(cancellationToken))
            .Where(s => !productId.HasValue ||
                NayaxProductMatcher.Match(products, s.NayaxProductId, s.ProductName)?.Id == productId.Value)
            .ToList();

        foreach (var sale in sales)
            await CostSaleAsync(sale, cancellationToken: cancellationToken);

        if (sales.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return sales.Count(s => s.CostingStatus == SaleCostingStatus.Costed);
    }

    public async Task<SaleCostingBackfillResult> BackfillAsync(bool dryRun = true, bool force = false, CancellationToken cancellationToken = default)
    {
        var sales = await _db.NayaxSales
            .Where(NayaxTransactionStatusClassifier.CompletedSalePredicate)
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

            await CostSaleAsync(sale, force: force, cancellationToken: cancellationToken);
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

    public async Task<NayaxCostBackfillResult> BackfillHistoricalCostsFromNayaxAsync(
        bool dryRun = true,
        bool force = false,
        DateTime? from = null,
        DateTime? to = null,
        long? productId = null,
        CancellationToken cancellationToken = default)
    {
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var query = _db.NayaxSales
            .Where(NayaxTransactionStatusClassifier.CompletedSalePredicate);
        if (from.HasValue)
            query = query.Where(s => s.MachineAuthorizationTime >= from.Value);
        if (to.HasValue)
            query = query.Where(s => s.MachineAuthorizationTime <= to.Value);

        var sales = (await query.OrderBy(s => s.MachineAuthorizationTime).ToListAsync(cancellationToken))
            .Where(s => !productId.HasValue ||
                NayaxProductMatcher.Match(products, s.NayaxProductId, s.ProductName)?.Id == productId.Value)
            .ToList();
        var withNayaxCost = sales.Count(s => s.NayaxProductCostPrice.HasValue);
        var invalid = sales.Count(s => s.NayaxProductCostPrice < 0);
        var alreadyCosted = sales.Count(s =>
            s.CostingStatus is SaleCostingStatus.Costed or SaleCostingStatus.LegacyEstimated);
        var candidates = sales.Where(s =>
            (force || s.CostingStatus is SaleCostingStatus.Pending or SaleCostingStatus.Error) &&
            s.NayaxProductCostPrice is >= 0).ToList();

        foreach (var sale in candidates)
        {
            sale.UnitCostAtSale = sale.NayaxProductCostPrice;
            sale.CostOfGoodsSold = sale.NayaxProductCostPrice;
            sale.CostingStatus = SaleCostingStatus.Costed;
            sale.CostSource = SaleCostSource.NayaxTransactionExport;
        }

        var stillPending = sales.Count(s =>
            !candidates.Contains(s) &&
            s.CostingStatus is SaleCostingStatus.Pending or SaleCostingStatus.Error);
        var errorRows = sales.Count(s =>
            !candidates.Contains(s) && s.CostingStatus == SaleCostingStatus.Error);

        if (dryRun)
            _db.ChangeTracker.Clear();
        else
            await _db.SaveChangesAsync(cancellationToken);

        return new NayaxCostBackfillResult(
            sales.Count, withNayaxCost, candidates.Count, alreadyCosted,
            stillPending, invalid, errorRows, dryRun);
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
