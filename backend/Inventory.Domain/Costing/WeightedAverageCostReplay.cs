using Inventory.Domain.Stock;

namespace Inventory.Domain.Costing;

/// <summary>
/// The perpetual weighted-average (AVCO) cost replay for one product. Extracted unchanged from the
/// former private <c>InventoryApi.Services.InventoryCostRebuildService.Replay</c>/<c>ApplyAdjustmentCost</c>
/// (issue #295, child 1 of #149): it replays the product's costing repairs, stock movements and
/// completed sales strictly after the optional transition baseline, in timestamp order (ties:
/// costing repairs, then costed restocks, then machine refills, then sales, then other movements;
/// then by source ID), and returns the resulting physical/costing quantity, inventory value,
/// average unit cost, the cost assigned to each movement and sale, and every data-quality issue
/// found. It never persists anything; the caller decides whether and how to apply the outcome.
///
/// A costing repair (issue #359) replays first at its own timestamp because it is the opening it
/// restores: anything else at that instant - a restock, a refill, the very sale the repair is meant
/// to cover - must see the repaired value already in the ledger.
/// </summary>
public static class WeightedAverageCostReplay
{
    private const int RepairPriority = 0;
    private const int RestockPriority = 1;
    private const int MachineRefillPriority = 2;
    private const int SalePriority = 3;
    private const int OtherAdjustmentPriority = 4;

    /// <summary>
    /// Replays the product's history. When <paramref name="targetSaleTime"/> is set, events after it
    /// are ignored. When <paramref name="targetSaleTransactionId"/> is set, the replay stops at that
    /// sale; otherwise, with a <paramref name="targetSaleTime"/>, it stops at the first sale exactly
    /// at that time. Either way it reports the average unit cost immediately before that sale.
    /// </summary>
    public static WeightedAverageCostReplayResult Replay(
        CostReplayProduct product,
        IReadOnlyCollection<CostReplayAdjustment> adjustments,
        IReadOnlyCollection<CostReplaySale> sales,
        IReadOnlyCollection<CostReplayRepair> repairs,
        CostReplayBaseline? baseline,
        long? targetSaleTransactionId = null,
        DateTime? targetSaleTime = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(adjustments);
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(repairs);

        var events = adjustments.Select(x => new CostEvent(x))
            .Concat(sales.Select(x => new CostEvent(x)))
            .Concat(repairs.Select(x => new CostEvent(x)))
            .Where(x => baseline is null || x.Timestamp > baseline.CutoffAt)
            .OrderBy(ReplayOrder)
            .ToList();
        var issues = new List<CostDataQualityIssue>();
        var adjustmentOutcomes = new List<CostReplayAdjustmentOutcome>();
        var repairOutcomes = new List<CostReplayRepairOutcome>();
        var saleCosts = new List<CostReplaySaleCost>();
        var uncostableSales = new List<CostReplaySale>();
        var physicalQuantity = baseline?.HomeStockQuantity ?? 0;
        var costingQuantity = baseline?.OpeningCostingQuantity ?? 0;
        decimal inventoryValue = baseline?.InventoryValue ?? 0;
        decimal? targetSaleUnitCost = null;
        var hasCostedAcquisition = baseline is not null;

        if (baseline is null && events.Count == 0 && product.QuantityInStock != 0 &&
            product.CostingQuantity is null && product.InventoryValue is null)
            issues.Add(new(CostDataQualityIssueCodes.MissingOpening, $"Product {product.ProductId} has physical stock but no opening stock movement."));

        foreach (var entry in events)
        {
            if (targetSaleTime.HasValue && entry.Timestamp > targetSaleTime.Value)
                break;

            if (entry.Sale is not null &&
                ((targetSaleTransactionId.HasValue && entry.Sale.TransactionId == targetSaleTransactionId.Value) ||
                 (!targetSaleTransactionId.HasValue && entry.Timestamp == targetSaleTime)))
            {
                targetSaleUnitCost = AverageUnitCost(costingQuantity, inventoryValue);
                break;
            }

            if (entry.Repair is { } repair)
            {
                var costingQuantityBefore = costingQuantity;
                var inventoryValueBefore = inventoryValue;
                costingQuantity = checked(costingQuantity + repair.Quantity);
                inventoryValue += repair.TotalValue;
                hasCostedAcquisition = true;
                repairOutcomes.Add(new(
                    repair,
                    costingQuantityBefore,
                    inventoryValueBefore,
                    costingQuantity,
                    inventoryValue,
                    AverageUnitCost(costingQuantity, inventoryValue)));
                continue;
            }

            if (entry.Adjustment is { } adjustment)
            {
                physicalQuantity = checked(physicalQuantity + adjustment.QuantityChange);
                if (physicalQuantity < 0)
                    issues.Add(new(CostDataQualityIssueCodes.NegativePhysicalStock, $"Product {product.ProductId} becomes physically negative at stock adjustment {adjustment.Id}."));

                var (assignedUnitCost, assignedTotalCost) = ApplyAdjustmentCost(
                    product.ProductId, adjustment, ref costingQuantity, ref inventoryValue, ref hasCostedAcquisition, issues);
                adjustmentOutcomes.Add(new(
                    adjustment,
                    physicalQuantity,
                    costingQuantity,
                    inventoryValue,
                    AverageUnitCost(costingQuantity, inventoryValue),
                    assignedUnitCost,
                    assignedTotalCost));
                continue;
            }

            var sale = entry.Sale!;
            var unitCost = AverageUnitCost(costingQuantity, inventoryValue);
            if (!unitCost.HasValue || unitCost.Value < 0)
            {
                issues.Add(new(hasCostedAcquisition ? CostDataQualityIssueCodes.UnknownCost : CostDataQualityIssueCodes.MissingOpening,
                    $"Completed Nayax sale {sale.TransactionId} for product {product.ProductId} has no known opening cost at {sale.AuthorizationTime:O}."));
                uncostableSales.Add(sale);
                continue;
            }

            costingQuantity--;
            if (costingQuantity < 0)
                issues.Add(new(CostDataQualityIssueCodes.NegativeCostingStock, $"Product {product.ProductId} becomes cost-negative at completed Nayax sale {sale.TransactionId}."));
            inventoryValue -= unitCost.Value;
            saleCosts.Add(new(sale, unitCost.Value));
        }

        if (baseline is null && product.CostingQuantity is null && product.InventoryValue is null &&
            product.QuantityInStock != physicalQuantity)
        {
            issues.Add(new(CostDataQualityIssueCodes.MissingOpening,
                $"Product {product.ProductId} stored physical quantity {product.QuantityInStock} does not reconcile to its movement history ({physicalQuantity})."));
        }

        return new(physicalQuantity, costingQuantity, inventoryValue, AverageUnitCost(costingQuantity, inventoryValue),
            targetSaleUnitCost, adjustmentOutcomes, repairOutcomes, saleCosts, uncostableSales, issues);
    }

    /// <summary>
    /// The weighted-average unit cost of the remaining costing inventory: <c>value / quantity</c>
    /// (unrounded), or <c>null</c> when the costing quantity is zero or negative.
    /// </summary>
    public static decimal? AverageUnitCost(int quantity, decimal value) =>
        quantity > 0 ? value / quantity : null;

    /// <summary>
    /// Whether <paramref name="repair"/> replays before <paramref name="sale"/>, decided by the
    /// replay's own ordering rather than by a timestamp comparison of its own (issue #359).
    ///
    /// The costing-repair apply uses this to verify placement, because a repair's effective time
    /// and a Nayax sale's authorization time are not recorded in the same time zone (an existing
    /// mismatch this does not fix): the only question that can be answered reliably is the one the
    /// replay itself asks, which is where the two events fall in this order. Both answers come from
    /// the same key, so they cannot drift apart.
    /// </summary>
    public static bool ReplaysBefore(CostReplayRepair repair, CostReplaySale sale)
    {
        ArgumentNullException.ThrowIfNull(repair);
        ArgumentNullException.ThrowIfNull(sale);

        return Comparer<(DateTime, int, long)>.Default.Compare(
            ReplayOrder(new CostEvent(repair)),
            ReplayOrder(new CostEvent(sale))) < 0;
    }

    /// <summary>
    /// The one replay ordering key: timestamp, then event priority, then source ID. Both the replay
    /// loop and <see cref="ReplaysBefore"/> read it, so there is a single definition of "before".
    /// </summary>
    private static (DateTime Timestamp, int Priority, long SourceId) ReplayOrder(CostEvent entry) =>
        (entry.Timestamp, entry.Priority, entry.SourceId);

    private static (decimal? UnitCost, decimal? TotalCost) ApplyAdjustmentCost(
        long productId,
        CostReplayAdjustment adjustment,
        ref int costingQuantity,
        ref decimal inventoryValue,
        ref bool hasCostedAcquisition,
        List<CostDataQualityIssue> issues)
    {
        if (adjustment.Reason == StockAdjustmentReason.Restock && adjustment.QuantityChange > 0)
        {
            if (!adjustment.UnitCost.HasValue || adjustment.UnitCost.Value < 0)
            {
                issues.Add(new(CostDataQualityIssueCodes.UnknownCost, $"Restock adjustment {adjustment.Id} for product {productId} has no valid unit cost."));
                return (null, null);
            }

            if (!adjustment.HasReceiptItemLink)
                issues.Add(new(CostDataQualityIssueCodes.LegacyUnlinkedCostedRestock,
                    $"Restock adjustment {adjustment.Id} for product {productId} is a legacy costed opening/purchase without a receipt item link."));

            costingQuantity = checked(costingQuantity + adjustment.QuantityChange);
            inventoryValue += adjustment.QuantityChange * adjustment.UnitCost.Value;
            hasCostedAcquisition = true;
            return (null, adjustment.QuantityChange * adjustment.UnitCost.Value);
        }

        if (adjustment.Reason == StockAdjustmentReason.Correction && adjustment.QuantityChange != 0)
        {
            var correctionUnitCost = AverageUnitCost(costingQuantity, inventoryValue);
            if (!correctionUnitCost.HasValue || correctionUnitCost.Value < 0)
            {
                issues.Add(new(hasCostedAcquisition ? CostDataQualityIssueCodes.UnknownCost : CostDataQualityIssueCodes.MissingOpening,
                    $"Correction adjustment {adjustment.Id} for product {productId} has no known cost."));
                return (null, null);
            }

            costingQuantity = checked(costingQuantity + adjustment.QuantityChange);
            if (costingQuantity < 0)
                issues.Add(new(CostDataQualityIssueCodes.NegativeCostingStock, $"Product {productId} becomes cost-negative at adjustment {adjustment.Id}."));
            inventoryValue += adjustment.QuantityChange * correctionUnitCost.Value;

            return (correctionUnitCost, Math.Abs(adjustment.QuantityChange) * correctionUnitCost.Value);
        }

        if (adjustment.Reason is not (StockAdjustmentReason.Damaged or StockAdjustmentReason.Expired or StockAdjustmentReason.Sale) ||
            adjustment.QuantityChange >= 0)
        {
            return (null, null);
        }

        var quantity = checked(-adjustment.QuantityChange);
        var unitCost = AverageUnitCost(costingQuantity, inventoryValue);
        if (!unitCost.HasValue || unitCost.Value < 0)
            issues.Add(new(hasCostedAcquisition ? CostDataQualityIssueCodes.UnknownCost : CostDataQualityIssueCodes.MissingOpening,
                $"{adjustment.Reason} adjustment {adjustment.Id} for product {productId} has no known cost."));

        costingQuantity -= quantity;
        if (costingQuantity < 0)
            issues.Add(new(CostDataQualityIssueCodes.NegativeCostingStock, $"Product {productId} becomes cost-negative at adjustment {adjustment.Id}."));
        if (unitCost.HasValue && unitCost.Value >= 0)
            inventoryValue -= quantity * unitCost.Value;

        return unitCost is >= 0 ? (unitCost, quantity * unitCost.Value) : (null, null);
    }

    private sealed class CostEvent
    {
        public CostEvent(CostReplayAdjustment adjustment)
        {
            Adjustment = adjustment;
            Timestamp = adjustment.EffectiveAt;
            Priority = adjustment.Reason == StockAdjustmentReason.Restock && adjustment.QuantityChange > 0 ? RestockPriority
                : adjustment.Reason == StockAdjustmentReason.MachineRefill ? MachineRefillPriority : OtherAdjustmentPriority;
            SourceId = adjustment.Id;
        }

        public CostEvent(CostReplaySale sale)
        {
            Sale = sale;
            Timestamp = sale.AuthorizationTime;
            Priority = SalePriority;
            SourceId = sale.TransactionId;
        }

        public CostEvent(CostReplayRepair repair)
        {
            Repair = repair;
            Timestamp = repair.EffectiveAt;
            Priority = RepairPriority;
            SourceId = repair.Id;
        }

        public CostReplayAdjustment? Adjustment { get; }
        public CostReplaySale? Sale { get; }
        public CostReplayRepair? Repair { get; }
        public DateTime Timestamp { get; }
        public int Priority { get; }
        public long SourceId { get; }
    }
}
