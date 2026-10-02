using Inventory.Domain.Stock;

namespace Inventory.Domain.Costing;

/// <summary>
/// A stock movement as the weighted-average cost replay sees it: the persisted facts of one
/// <c>StockAdjustment</c> row, without its persistence entity. <see cref="HasReceiptItemLink"/>
/// is whether the movement is linked to a purchase (receipt) item.
/// </summary>
public sealed record CostReplayAdjustment(
    int Id,
    DateTime EffectiveAt,
    StockAdjustmentReason Reason,
    int QuantityChange,
    decimal? UnitCost,
    bool HasReceiptItemLink);

/// <summary>A completed sale of the product, identified by its Nayax transaction ID.</summary>
public sealed record CostReplaySale(long TransactionId, DateTime AuthorizationTime);

/// <summary>
/// An inventory-cost transition baseline: the authoritative opening state at <see cref="CutoffAt"/>.
/// Only events strictly after the cutoff are replayed on top of it.
/// </summary>
public sealed record CostReplayBaseline(
    DateTime CutoffAt,
    int HomeStockQuantity,
    int OpeningCostingQuantity,
    decimal InventoryValue);

/// <summary>
/// The product's currently stored stock and costing state, used only to detect a missing opening
/// (stored physical stock that the movement history cannot explain).
/// </summary>
public sealed record CostReplayProduct(
    long ProductId,
    int QuantityInStock,
    int? CostingQuantity,
    decimal? InventoryValue);

/// <summary>A data-quality problem found while replaying a product's cost history.</summary>
public sealed record CostDataQualityIssue(string Code, string Message)
{
    /// <summary>Whether this issue makes the replayed cost unreliable (see <see cref="CostDataQualityIssueCodes.IsFatal"/>).</summary>
    public bool IsFatal => CostDataQualityIssueCodes.IsFatal(Code);
}

/// <summary>The data-quality issue codes the replay reports, and their fatal/non-fatal split.</summary>
public static class CostDataQualityIssueCodes
{
    public const string MissingOpening = "MissingOpening";
    public const string UnknownCost = "UnknownCost";
    public const string NegativePhysicalStock = "NegativePhysicalStock";
    public const string NegativeCostingStock = "NegativeCostingStock";

    /// <summary>Non-fatal: a costed restock that predates receipt-item linking.</summary>
    public const string LegacyUnlinkedCostedRestock = "LegacyUnlinkedCostedRestock";

    public static bool IsFatal(string code) =>
        code is MissingOpening or UnknownCost or NegativePhysicalStock or NegativeCostingStock;
}

/// <summary>
/// The running state after one replayed stock movement, plus the cost the replay assigned to it.
/// <see cref="AssignedUnitCost"/>/<see cref="AssignedTotalCost"/> are <c>null</c> when the replay
/// leaves the movement's persisted value unchanged.
/// </summary>
public sealed record CostReplayAdjustmentOutcome(
    CostReplayAdjustment Adjustment,
    int QuantityAfter,
    int CostingQuantityAfter,
    decimal InventoryValueAfter,
    decimal? AverageUnitCostAfter,
    decimal? AssignedUnitCost,
    decimal? AssignedTotalCost);

/// <summary>The weighted-average unit cost a replayed completed sale consumed.</summary>
public sealed record CostReplaySaleCost(CostReplaySale Sale, decimal UnitCost);

/// <summary>
/// The result of replaying a product's cost history. <see cref="TargetSaleUnitCost"/> is the
/// average unit cost immediately before the requested target sale, when one was requested and
/// reached; <see cref="Adjustments"/> lists every replayed stock movement and
/// <see cref="SaleCosts"/> every completed sale the replay costed, both in replay order.
/// </summary>
public sealed record WeightedAverageCostReplayResult(
    int PhysicalQuantity,
    int CostingQuantity,
    decimal InventoryValue,
    decimal? AverageUnitCost,
    decimal? TargetSaleUnitCost,
    IReadOnlyList<CostReplayAdjustmentOutcome> Adjustments,
    IReadOnlyList<CostReplaySaleCost> SaleCosts,
    IReadOnlyList<CostDataQualityIssue> Issues)
{
    public bool HasFatalIssue => Issues.Any(issue => issue.IsFatal);
}
