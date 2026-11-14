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

/// <summary>
/// A completed sale of the product, identified by its Nayax transaction ID.
/// <see cref="NayaxProductCostPrice"/> is the transaction-level Nayax <c>Product Cost Price</c>
/// persisted on the sale, or <see langword="null"/> when Nayax sent none: precedence step 2 of the
/// historical sale cost, which the replay uses only for a sale the ledger cannot cost.
/// </summary>
public sealed record CostReplaySale(long TransactionId, DateTime AuthorizationTime, decimal? NayaxProductCostPrice = null);

/// <summary>
/// A costing-only historical repair as the replay sees it (issue #359): the persisted facts of one
/// append-only costing repair record, without its persistence entity.
///
/// It is a costing acquisition and nothing else. It increases costing quantity and inventory value
/// at <see cref="UnitCost"/> and never touches physical storage stock, machine stock, or any stock
/// movement, which is exactly what distinguishes it from a Restock, Correction or MachineRefill.
/// <see cref="Quantity"/> is always positive and <see cref="UnitCost"/> never negative
/// (<see cref="CostingRepairPolicy"/> is where that is enforced).
/// </summary>
public sealed record CostReplayRepair(int Id, DateTime EffectiveAt, int Quantity, decimal UnitCost)
{
    /// <summary>The inventory value the repair adds: <c>quantity * unit cost</c>, unrounded.</summary>
    public decimal TotalValue => Quantity * UnitCost;
}

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
///
/// <see cref="Name"/> is the product's name for the replay's data-quality messages (issue #833) and
/// nothing else: it is optional because a caller may not have loaded one, it changes no quantity,
/// cost or issue classification, and it is deliberately not part of
/// <see cref="CostLedgerFingerprint"/>, so renaming a product never invalidates an outstanding
/// costing-repair preview.
/// </summary>
public sealed record CostReplayProduct(
    long ProductId,
    int QuantityInStock,
    int? CostingQuantity,
    decimal? InventoryValue,
    string? Name = null);

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

    /// <summary>
    /// Non-fatal: a completed sale had no known ledger cost (no recorded costed stock before it) and
    /// was costed at the latest costed purchase before it, or else at its own Nayax
    /// <c>Product Cost Price</c>.
    /// </summary>
    public const string CostFallback = "CostFallback";

    /// <summary>
    /// Non-fatal: a completed sale that none of the historical sale-cost precedence steps could cost
    /// (no ledger cost, no earlier costed purchase and no valid Nayax <c>Product Cost Price</c>), so
    /// it is left uncosted - precedence step 4 of <c>AGENTS.md</c> § Inventory and historical costing
    /// invariants. It consumes no costing quantity or value and, being non-fatal, never blocks the
    /// product's later stock writes (issue #1239).
    /// </summary>
    public const string UncostedSale = "UncostedSale";

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

/// <summary>
/// The running costing state immediately before and after one replayed costing repair (issue
/// #359). Nothing is written back to the repair record - it is immutable - so this exists for the
/// repair preview, which has to show the operator the position the repair lands on.
/// </summary>
public sealed record CostReplayRepairOutcome(
    CostReplayRepair Repair,
    int CostingQuantityBefore,
    decimal InventoryValueBefore,
    int CostingQuantityAfter,
    decimal InventoryValueAfter,
    decimal? AverageUnitCostAfter);

/// <summary>
/// Where the unit cost the replay assigned to a completed sale came from.
/// </summary>
public enum CostReplaySaleCostSource
{
    /// <summary>The weighted-average cost of the costed stock the sale consumed from the ledger.</summary>
    Ledger = 0,

    /// <summary>
    /// The ledger had no known cost before the sale (no costed stock recorded, or it had run out), so
    /// the sale was costed at the unit cost of the latest costed purchase (restock) replayed before
    /// it - an estimate, reported as such.
    /// </summary>
    LastPurchase = 1,

    /// <summary>
    /// The ledger had no known cost and no costed purchase had been replayed before the sale, so the
    /// sale was costed at its own transaction-level Nayax <c>Product Cost Price</c>.
    /// </summary>
    NayaxCost = 2,
}

/// <summary>
/// The unit cost the replay assigned to a completed sale, and where it came from. A sale costed by
/// <see cref="CostReplaySaleCostSource.LastPurchase"/> or <see cref="CostReplaySaleCostSource.NayaxCost"/>
/// consumes no costing inventory, because the ledger never held the unit it sold: recording the
/// missing purchase or a costing repair before it makes the next replay cost it from the ledger.
/// </summary>
public sealed record CostReplaySaleCost(
    CostReplaySale Sale,
    decimal UnitCost,
    CostReplaySaleCostSource Source = CostReplaySaleCostSource.Ledger);

/// <summary>
/// The result of replaying a product's cost history. <see cref="TargetSaleUnitCost"/> is the
/// average unit cost immediately before the requested target sale, when one was requested and
/// reached; <see cref="Adjustments"/> lists every replayed stock movement, <see cref="Repairs"/>
/// every replayed costing repair, <see cref="SaleCosts"/> every completed sale the replay costed
/// and <see cref="UncostableSales"/> every completed sale it could not cost, all in replay order.
/// </summary>
public sealed record WeightedAverageCostReplayResult(
    int PhysicalQuantity,
    int CostingQuantity,
    decimal InventoryValue,
    decimal? AverageUnitCost,
    decimal? TargetSaleUnitCost,
    IReadOnlyList<CostReplayAdjustmentOutcome> Adjustments,
    IReadOnlyList<CostReplayRepairOutcome> Repairs,
    IReadOnlyList<CostReplaySaleCost> SaleCosts,
    IReadOnlyList<CostReplaySale> UncostableSales,
    IReadOnlyList<CostDataQualityIssue> Issues)
{
    public bool HasFatalIssue => Issues.Any(issue => issue.IsFatal);
}
