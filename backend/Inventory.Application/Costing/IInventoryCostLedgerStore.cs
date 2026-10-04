using Inventory.Domain.Costing;

namespace Inventory.Application.Costing;

/// <summary>
/// One product's cost history as the weighted-average replay consumes it (issue #296). It is not
/// sealed so the store implementation can carry the persistence entities each replay input came
/// from, and write the replay's outcome back to exactly those rows.
/// </summary>
public class InventoryCostLedger
{
    public InventoryCostLedger(
        CostReplayProduct product,
        IReadOnlyCollection<CostReplayAdjustment> adjustments,
        IReadOnlyCollection<CostReplaySale> sales,
        IReadOnlyCollection<CostReplayRepair> repairs,
        CostReplayBaseline? baseline)
    {
        Product = product;
        Adjustments = adjustments;
        Sales = sales;
        Repairs = repairs;
        Baseline = baseline;
    }

    public CostReplayProduct Product { get; }

    public IReadOnlyCollection<CostReplayAdjustment> Adjustments { get; }

    /// <summary>The product's completed sales only.</summary>
    public IReadOnlyCollection<CostReplaySale> Sales { get; }

    /// <summary>
    /// The product's costing-only historical repairs (issue #359). Stated explicitly rather than
    /// defaulted to empty: a ledger that silently omitted them would under-value the product's
    /// costing inventory and re-break the sales a repair was applied to unblock.
    /// </summary>
    public IReadOnlyCollection<CostReplayRepair> Repairs { get; }

    /// <summary>The latest inventory-cost transition baseline in range, if any.</summary>
    public CostReplayBaseline? Baseline { get; }
}

/// <summary>
/// The product's rebuilt perpetual costing position, as persisted on the product. A
/// <see cref="PhysicalQuantity"/> of <c>null</c> leaves the stored physical quantity unchanged, the
/// way a null assigned movement cost leaves the persisted one unchanged: a costing-only rebuild
/// (issue #359) has no authority over physical stock.
/// </summary>
public sealed record ProductCostPosition(
    int? PhysicalQuantity,
    int CostingQuantity,
    decimal InventoryValue,
    decimal AverageUnitCost);

/// <summary>
/// Narrow persistence port for the product cost rebuild (issue #296), owned by the Application
/// layer. Its implementation loads the business-scoped ledger and stages the replay outcome the
/// use case decided on in the caller's unit of work; it never saves, opens a transaction, or
/// decides a cost, so a rebuild still commits or rolls back with the caller's transaction.
/// </summary>
public interface IInventoryCostLedgerStore
{
    /// <summary>
    /// The product's full ledger. With <paramref name="forUpdate"/> the rows are change-tracked so
    /// the Stage methods can update them; without it (a dry run) nothing is tracked. <c>null</c>
    /// when the product does not exist (or is not owned by the caller's business).
    /// </summary>
    Task<InventoryCostLedger?> LoadAsync(long productId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>
    /// The read-only ledger as of <paramref name="asOf"/>: movements effective, completed sales
    /// authorised, and the latest baseline cut off at or before it. <c>null</c> when the product
    /// does not exist (or is not owned by the caller's business).
    /// </summary>
    Task<InventoryCostLedger?> LoadAsOfAsync(long productId, DateTime asOf, CancellationToken cancellationToken);

    /// <summary>
    /// Stages each replayed movement's running position and assigned cost (an
    /// <see cref="CostReplayAdjustmentOutcome.AssignedUnitCost"/>/<see cref="CostReplayAdjustmentOutcome.AssignedTotalCost"/>
    /// of <c>null</c> leaves the persisted value unchanged), and costs each listed completed sale
    /// from the inventory ledger at its <see cref="CostReplaySaleCost.UnitCost"/>.
    /// </summary>
    void StageReplay(
        InventoryCostLedger ledger,
        IReadOnlyCollection<CostReplayAdjustmentOutcome> adjustments,
        IReadOnlyCollection<CostReplaySaleCost> recostedSales);

    /// <summary>
    /// Stages the product's rebuilt costing position, and its physical quantity unless
    /// <see cref="ProductCostPosition.PhysicalQuantity"/> is <c>null</c>.
    /// </summary>
    void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position);
}
