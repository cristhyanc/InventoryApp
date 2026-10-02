using Inventory.Domain.Costing;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Services;

public sealed class InventoryCostRebuildService : IInventoryCostRebuildService
{
    private readonly AppDbContext _db;

    public InventoryCostRebuildService(AppDbContext db) => _db = db;

    public async Task<InventoryCostRebuildResult> RebuildAsync(
        long productId,
        DateTime? recostCompletedSalesFrom = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var productQuery = dryRun ? _db.Products.AsNoTracking() : _db.Products.AsQueryable();
        var product = await productQuery.SingleOrDefaultAsync(p => p.Id == productId, cancellationToken)
            ?? throw new InvalidOperationException($"Product {productId} does not exist.");
        var adjustmentQuery = dryRun ? _db.StockAdjustments.AsNoTracking() : _db.StockAdjustments.AsQueryable();
        var saleQuery = dryRun ? _db.NayaxSales.AsNoTracking() : _db.NayaxSales.AsQueryable();
        var adjustments = await adjustmentQuery.Where(x => x.ProductId == productId).ToListAsync(cancellationToken);
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var sales = (await saleQuery
                .Where(EfNayaxSalesQueries.CompletedSalePredicate)
                .ToListAsync(cancellationToken))
            .Where(s => NayaxProductMatcher.Match(products, s.NayaxProductId, s.ProductName)?.Id == productId)
            .ToList();
        var baseline = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => x.ProductId == productId)
            .OrderByDescending(x => x.CutoffAt)
            .FirstOrDefaultAsync(cancellationToken);

        var outcome = Replay(product, adjustments, sales, baseline, recostCompletedSalesFrom, mutate: !dryRun);
        var result = ToResult(productId, outcome, dryRun);
        ThrowIfFatal(result);

        if (!dryRun)
        {
            product.QuantityInStock = outcome.PhysicalQuantity;
            product.CostingQuantity = outcome.CostingQuantity;
            product.InventoryValue = outcome.InventoryValue;
            product.AverageUnitCost = outcome.AverageUnitCost ?? 0m;
            product.UpdatedAt = DateTime.UtcNow;
        }

        return result;
    }

    public async Task<decimal?> GetAverageUnitCostAtAsync(
        long productId,
        DateTime saleTime,
        long? saleTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        var product = await _db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
            return null;

        var adjustments = await _db.StockAdjustments.AsNoTracking()
            .Where(x => x.ProductId == productId && x.EffectiveAt <= saleTime)
            .ToListAsync(cancellationToken);
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var sales = (await _db.NayaxSales.AsNoTracking()
                .Where(EfNayaxSalesQueries.CompletedSalePredicate)
                .Where(s => s.MachineAuthorizationTime <= saleTime)
                .ToListAsync(cancellationToken))
            .Where(s => NayaxProductMatcher.Match(products, s.NayaxProductId, s.ProductName)?.Id == productId)
            .ToList();
        var baseline = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => x.ProductId == productId && x.CutoffAt <= saleTime)
            .OrderByDescending(x => x.CutoffAt)
            .FirstOrDefaultAsync(cancellationToken);

        var outcome = Replay(product, adjustments, sales, baseline, null, mutate: false, saleTransactionId, saleTime);
        return outcome.Issues.Any(IsFatal) ? null : outcome.TargetSaleUnitCost ?? outcome.AverageUnitCost;
    }

    private static ReplayOutcome Replay(
        Product product,
        IReadOnlyCollection<StockAdjustment> adjustments,
        IReadOnlyCollection<NayaxSales> sales,
        InventoryCostTransitionBaseline? baseline,
        DateTime? recostCompletedSalesFrom,
        bool mutate,
        long? targetSaleTransactionId = null,
        DateTime? targetSaleTime = null)
    {
        var adjustmentInputs = new List<CostReplayAdjustment>(adjustments.Count);
        var adjustmentsByInput = new Dictionary<CostReplayAdjustment, StockAdjustment>(ReferenceEqualityComparer.Instance);
        foreach (var adjustment in adjustments)
        {
            var input = ToDomain(adjustment);
            adjustmentInputs.Add(input);
            adjustmentsByInput.Add(input, adjustment);
        }

        var saleInputs = new List<CostReplaySale>(sales.Count);
        var salesByInput = new Dictionary<CostReplaySale, NayaxSales>(ReferenceEqualityComparer.Instance);
        foreach (var sale in sales)
        {
            var input = new CostReplaySale(sale.TransactionID, sale.MachineAuthorizationTime);
            saleInputs.Add(input);
            salesByInput.Add(input, sale);
        }

        var replay = WeightedAverageCostReplay.Replay(
            new CostReplayProduct(product.Id, product.QuantityInStock, product.CostingQuantity, product.InventoryValue),
            adjustmentInputs,
            saleInputs,
            baseline is null
                ? null
                : new CostReplayBaseline(baseline.CutoffAt, baseline.HomeStockQuantity, baseline.OpeningCostingQuantity, baseline.InventoryValue),
            targetSaleTransactionId,
            targetSaleTime);

        var recostedSaleCount = 0;
        if (mutate)
        {
            foreach (var outcome in replay.Adjustments)
            {
                var adjustment = adjustmentsByInput[outcome.Adjustment];
                adjustment.QuantityAfter = outcome.QuantityAfter;
                adjustment.CostingQuantityAfter = outcome.CostingQuantityAfter;
                adjustment.InventoryValueAfter = outcome.InventoryValueAfter;
                adjustment.AverageUnitCostAfter = outcome.AverageUnitCostAfter;
                if (outcome.AssignedUnitCost.HasValue)
                    adjustment.UnitCost = outcome.AssignedUnitCost;
                if (outcome.AssignedTotalCost.HasValue)
                    adjustment.TotalCost = outcome.AssignedTotalCost;
            }

            if (recostCompletedSalesFrom.HasValue)
            {
                foreach (var saleCost in replay.SaleCosts.Where(x => x.Sale.AuthorizationTime >= recostCompletedSalesFrom.Value))
                {
                    var sale = salesByInput[saleCost.Sale];
                    sale.UnitCostAtSale = saleCost.UnitCost;
                    sale.CostOfGoodsSold = saleCost.UnitCost;
                    sale.CostingStatus = SaleCostingStatus.Costed;
                    sale.CostSource = SaleCostSource.InventoryLedger;
                    recostedSaleCount++;
                }
            }
        }

        return new(replay.PhysicalQuantity, replay.CostingQuantity, replay.InventoryValue, replay.AverageUnitCost,
            replay.TargetSaleUnitCost, recostedSaleCount,
            replay.Issues.Select(x => new InventoryCostDataQualityIssue(x.Code, x.Message)).ToList());
    }

    private static CostReplayAdjustment ToDomain(StockAdjustment adjustment) =>
        new(
            adjustment.Id,
            adjustment.EffectiveAt,
            (DomainStock.StockAdjustmentReason)adjustment.Reason,
            adjustment.QuantityChange,
            adjustment.UnitCost,
            adjustment.ReceiptItemId.HasValue);

    private static InventoryCostRebuildResult ToResult(long productId, ReplayOutcome outcome, bool dryRun) =>
        new()
        {
            ProductId = productId,
            PhysicalQuantity = outcome.PhysicalQuantity,
            CostingQuantity = outcome.CostingQuantity,
            InventoryValue = outcome.InventoryValue,
            AverageUnitCost = outcome.AverageUnitCost,
            RecostedSaleCount = outcome.RecostedSaleCount,
            DryRun = dryRun,
            Issues = outcome.Issues
        };

    private static bool IsFatal(InventoryCostDataQualityIssue issue) =>
        CostDataQualityIssueCodes.IsFatal(issue.Code);

    private static void ThrowIfFatal(InventoryCostRebuildResult result)
    {
        var fatal = result.Issues.Where(IsFatal).Select(x => x.Message).Distinct().ToArray();
        if (fatal.Length > 0 && !result.DryRun)
            throw new InventoryCostDataQualityException(string.Join(" ", fatal));
    }

    private sealed record ReplayOutcome(
        int PhysicalQuantity,
        int CostingQuantity,
        decimal InventoryValue,
        decimal? AverageUnitCost,
        decimal? TargetSaleUnitCost,
        int RecostedSaleCount,
        IReadOnlyList<InventoryCostDataQualityIssue> Issues);
}
