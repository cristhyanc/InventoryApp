using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// The preview a costing repair would produce, plus the Domain values it was computed from, so the
/// apply can enforce its placement rule against exactly the projection it validated.
/// </summary>
internal sealed record InventoryCostRepairProjectionResult(
    InventoryCostRepairPreview Preview,
    CostReplayRepair PendingRepair,
    CostReplaySale? FirstUncostableSale);

/// <summary>
/// Projects a proposed costing repair onto one product's cost ledger (issue #359), shared by
/// <see cref="PreviewInventoryCostRepair"/> and <see cref="ApplyInventoryCostRepair"/> so the
/// numbers an operator approves and the numbers the apply validates come from one calculation.
///
/// It replays the ledger twice through <see cref="WeightedAverageCostReplay"/> - once as it stands,
/// once with the proposed repair added - because the two answer different questions: the current
/// replay says which completed sale is uncostable today, and the repaired replay says where the
/// history ends up and what is still fatal afterwards. It persists nothing and reads the same
/// ledger the rebuild does, through <see cref="IInventoryCostLedgerStore"/>.
/// </summary>
internal sealed class InventoryCostRepairProjection
{
    /// <summary>
    /// The key the not-yet-stored repair replays under. <see cref="int.MaxValue"/> rather than
    /// <c>0</c> so it sorts after the product's existing repairs at the same instant, which is
    /// where the appended row - with the highest key - will actually land.
    /// </summary>
    private const int PendingRepairId = int.MaxValue;

    private readonly IInventoryCostRepairStore _store;
    private readonly IInventoryCostLedgerStore _ledgerStore;

    public InventoryCostRepairProjection(IInventoryCostRepairStore store, IInventoryCostLedgerStore ledgerStore)
    {
        _store = store;
        _ledgerStore = ledgerStore;
    }

    public async Task<InventoryCostRepairProjectionResult> BuildAsync(
        InventoryCostRepairRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repair = CostingRepairPolicy.Validate(
            request.EffectiveAt, request.Quantity, request.UnitCost, request.Reason);
        var product = await _store.GetProductAsync(request.ProductId, cancellationToken)
            ?? throw new DomainValidationException($"Product {request.ProductId} does not exist.");
        var ledger = await _ledgerStore.LoadAsync(request.ProductId, forUpdate: false, cancellationToken)
            ?? throw new DomainValidationException($"Product {request.ProductId} does not exist.");
        CostingRepairPolicy.EnsureAfterBaselineCutoff(repair.EffectiveAt, ledger.Baseline?.CutoffAt);

        var pending = new CostReplayRepair(PendingRepairId, repair.EffectiveAt, repair.Quantity, repair.UnitCost);
        var current = WeightedAverageCostReplay.Replay(
            ledger.Product, ledger.Adjustments, ledger.Sales, ledger.Repairs, ledger.Baseline);
        var repaired = WeightedAverageCostReplay.Replay(
            ledger.Product, ledger.Adjustments, ledger.Sales, [.. ledger.Repairs, pending], ledger.Baseline);
        var outcome = repaired.Repairs.Single(x => x.Repair.Id == PendingRepairId);
        var firstUncostableSale = current.UncostableSales.FirstOrDefault();

        var preview = new InventoryCostRepairPreview(
            product.Id,
            product.Name,
            repair.EffectiveAt,
            repair.Quantity,
            repair.UnitCost,
            repair.TotalValue,
            repair.Reason,
            outcome.CostingQuantityBefore,
            outcome.InventoryValueBefore,
            outcome.CostingQuantityAfter,
            outcome.InventoryValueAfter,
            outcome.AverageUnitCostAfter,
            firstUncostableSale is null
                ? null
                : new UncostableSale(firstUncostableSale.TransactionId, firstUncostableSale.AuthorizationTime),
            // Vacuously true when there is nothing uncostable to be ahead of: the repair is then
            // not covering a sale at all, and the apply has no placement to reject.
            firstUncostableSale is null || WeightedAverageCostReplay.ReplaysBefore(pending, firstUncostableSale),
            repaired.CostingQuantity,
            repaired.InventoryValue,
            repaired.AverageUnitCost,
            repaired.Issues
                .Where(issue => issue.IsFatal)
                .Select(issue => new InventoryCostDataQualityIssue(issue.Code, issue.Message))
                .DistinctBy(issue => issue.Message)
                .ToList(),
            CostLedgerFingerprint.Compute(
                ledger.Product, ledger.Adjustments, ledger.Sales, ledger.Repairs, ledger.Baseline));

        return new(preview, pending, firstUncostableSale);
    }
}
