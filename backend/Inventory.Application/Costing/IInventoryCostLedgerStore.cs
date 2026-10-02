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
        CostReplayBaseline? baseline)
    {
        Product = product;
        Adjustments = adjustments;
        Sales = sales;
        Baseline = baseline;
    }

    public CostReplayProduct Product { get; }

    public IReadOnlyCollection<CostReplayAdjustment> Adjustments { get; }

    /// <summary>The product's completed sales only.</summary>
    public IReadOnlyCollection<CostReplaySale> Sales { get; }

    /// <summary>The latest inventory-cost transition baseline in range, if any.</summary>
    public CostReplayBaseline? Baseline { get; }
}

/// <summary>The product's rebuilt perpetual costing position, as persisted on the product.</summary>
public sealed record ProductCostPosition(
    int PhysicalQuantity,
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

    /// <summary>Stages the product's rebuilt physical and costing position.</summary>
    void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position);
}
