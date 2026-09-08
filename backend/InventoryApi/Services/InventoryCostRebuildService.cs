using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

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
                .Where(NayaxTransactionStatusClassifier.CompletedSalePredicate)
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
                .Where(NayaxTransactionStatusClassifier.CompletedSalePredicate)
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
        var events = adjustments.Select(x => new CostEvent(x)).Cast<CostEvent>()
            .Concat(sales.Select(x => new CostEvent(x)))
            .Where(x => baseline is null || x.Timestamp > baseline.CutoffAt)
            .OrderBy(x => x.Timestamp)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.SourceId)
            .ToList();
        var issues = new List<InventoryCostDataQualityIssue>();
        var physicalQuantity = baseline?.HomeStockQuantity ?? 0;
        var costingQuantity = baseline?.OpeningCostingQuantity ?? 0;
        decimal inventoryValue = baseline?.InventoryValue ?? 0;
        decimal? targetSaleUnitCost = null;
        var hasCostedAcquisition = baseline is not null;
        var recostedSaleCount = 0;

        if (baseline is null && events.Count == 0 && product.QuantityInStock != 0 &&
            product.CostingQuantity is null && product.InventoryValue is null)
            issues.Add(new("MissingOpening", $"Product {product.Id} has physical stock but no opening stock movement."));

        foreach (var entry in events)
        {
            if (targetSaleTime.HasValue && entry.Timestamp > targetSaleTime.Value)
                break;

            if (entry.Sale is not null &&
                ((targetSaleTransactionId.HasValue && entry.Sale.TransactionID == targetSaleTransactionId.Value) ||
                 (!targetSaleTransactionId.HasValue && entry.Timestamp == targetSaleTime)))
            {
                targetSaleUnitCost = AverageUnitCost(costingQuantity, inventoryValue);
                break;
            }

            if (entry.Adjustment is { } adjustment)
            {
                physicalQuantity = checked(physicalQuantity + adjustment.QuantityChange);
                if (physicalQuantity < 0)
                    issues.Add(new("NegativePhysicalStock", $"Product {product.Id} becomes physically negative at stock adjustment {adjustment.Id}."));

                ApplyAdjustmentCost(product.Id, adjustment, ref costingQuantity, ref inventoryValue, ref hasCostedAcquisition, issues, mutate);
                if (mutate)
                {
                    adjustment.QuantityAfter = physicalQuantity;
                    adjustment.CostingQuantityAfter = costingQuantity;
                    adjustment.InventoryValueAfter = inventoryValue;
                    adjustment.AverageUnitCostAfter = AverageUnitCost(costingQuantity, inventoryValue);
                }
                continue;
            }

            var sale = entry.Sale!;
            var unitCost = AverageUnitCost(costingQuantity, inventoryValue);
            if (!unitCost.HasValue || unitCost.Value < 0)
            {
                issues.Add(new(hasCostedAcquisition ? "UnknownCost" : "MissingOpening",
                    $"Completed Nayax sale {sale.TransactionID} for product {product.Id} has no known opening cost at {sale.MachineAuthorizationTime:O}."));
                continue;
            }

            costingQuantity--;
            if (costingQuantity < 0)
                issues.Add(new("NegativeCostingStock", $"Product {product.Id} becomes cost-negative at completed Nayax sale {sale.TransactionID}."));
            inventoryValue -= unitCost.Value;

            if (mutate && recostCompletedSalesFrom.HasValue && sale.MachineAuthorizationTime >= recostCompletedSalesFrom.Value)
            {
                sale.UnitCostAtSale = unitCost is >= 0 ? unitCost : null;
                sale.CostOfGoodsSold = unitCost is >= 0 ? unitCost : null;
                sale.CostingStatus = unitCost is >= 0 ? SaleCostingStatus.Costed : SaleCostingStatus.Pending;
                sale.CostSource = unitCost is >= 0 ? SaleCostSource.InventoryLedger : SaleCostSource.Unknown;
                recostedSaleCount++;
            }
        }

        if (baseline is null && product.CostingQuantity is null && product.InventoryValue is null &&
            product.QuantityInStock != physicalQuantity)
        {
            issues.Add(new("MissingOpening",
                $"Product {product.Id} stored physical quantity {product.QuantityInStock} does not reconcile to its movement history ({physicalQuantity})."));
        }

        return new(physicalQuantity, costingQuantity, inventoryValue, AverageUnitCost(costingQuantity, inventoryValue),
            targetSaleUnitCost, recostedSaleCount, issues);
    }

    private static void ApplyAdjustmentCost(
        long productId,
        StockAdjustment adjustment,
        ref int costingQuantity,
        ref decimal inventoryValue,
        ref bool hasCostedAcquisition,
        ICollection<InventoryCostDataQualityIssue> issues,
        bool mutate)
    {
        if (adjustment.Reason == StockAdjustmentReason.Restock && adjustment.QuantityChange > 0)
        {
            if (!adjustment.UnitCost.HasValue || adjustment.UnitCost.Value < 0)
            {
                issues.Add(new("UnknownCost", $"Restock adjustment {adjustment.Id} for product {productId} has no valid unit cost."));
                return;
            }

            if (!adjustment.ReceiptItemId.HasValue)
                issues.Add(new("LegacyUnlinkedCostedRestock",
                    $"Restock adjustment {adjustment.Id} for product {productId} is a legacy costed opening/purchase without a receipt item link."));

            costingQuantity = checked(costingQuantity + adjustment.QuantityChange);
            inventoryValue += adjustment.QuantityChange * adjustment.UnitCost.Value;
            hasCostedAcquisition = true;
            if (mutate)
                adjustment.TotalCost = adjustment.QuantityChange * adjustment.UnitCost.Value;
            return;
        }

        if (adjustment.Reason is not (StockAdjustmentReason.Damaged or StockAdjustmentReason.Expired or StockAdjustmentReason.Sale) ||
            adjustment.QuantityChange >= 0)
        {
            if (adjustment.QuantityChange > 0 && adjustment.Reason == StockAdjustmentReason.Correction)
                issues.Add(new("UnknownCost", $"Positive correction adjustment {adjustment.Id} for product {productId} has no explicit cost."));
            return;
        }

        var quantity = checked(-adjustment.QuantityChange);
        var unitCost = AverageUnitCost(costingQuantity, inventoryValue);
        if (!unitCost.HasValue || unitCost.Value < 0)
            issues.Add(new(hasCostedAcquisition ? "UnknownCost" : "MissingOpening",
                $"{adjustment.Reason} adjustment {adjustment.Id} for product {productId} has no known cost."));

        costingQuantity -= quantity;
        if (costingQuantity < 0)
            issues.Add(new("NegativeCostingStock", $"Product {productId} becomes cost-negative at adjustment {adjustment.Id}."));
        if (unitCost.HasValue && unitCost.Value >= 0)
            inventoryValue -= quantity * unitCost.Value;

        if (mutate && unitCost is >= 0)
        {
            adjustment.UnitCost = unitCost;
            adjustment.TotalCost = quantity * unitCost.Value;
        }
    }

    private static decimal? AverageUnitCost(int quantity, decimal value) =>
        quantity > 0 ? value / quantity : null;

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
        issue.Code is "MissingOpening" or "UnknownCost" or "NegativePhysicalStock" or "NegativeCostingStock";

    private static void ThrowIfFatal(InventoryCostRebuildResult result)
    {
        var fatal = result.Issues.Where(IsFatal).Select(x => x.Message).Distinct().ToArray();
        if (fatal.Length > 0 && !result.DryRun)
            throw new InventoryCostDataQualityException(string.Join(" ", fatal));
    }

    private sealed class CostEvent
    {
        public CostEvent(StockAdjustment adjustment)
        {
            Adjustment = adjustment;
            Timestamp = adjustment.EffectiveAt;
            Priority = adjustment.Reason == StockAdjustmentReason.Restock && adjustment.QuantityChange > 0 ? 0
                : adjustment.Reason == StockAdjustmentReason.MachineRefill ? 1 : 3;
            SourceId = adjustment.Id;
        }

        public CostEvent(NayaxSales sale)
        {
            Sale = sale;
            Timestamp = sale.MachineAuthorizationTime;
            Priority = 2;
            SourceId = sale.TransactionID;
        }

        public StockAdjustment? Adjustment { get; }
        public NayaxSales? Sale { get; }
        public DateTime Timestamp { get; }
        public int Priority { get; }
        public long SourceId { get; }
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
