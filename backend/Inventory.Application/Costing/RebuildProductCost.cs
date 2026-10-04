using Inventory.Domain.Costing;

namespace Inventory.Application.Costing;

/// <summary>
/// Rebuilds a product's perpetual weighted-average cost (issue #296, child 2 of #149), moved
/// unchanged from the former <c>InventoryApi.Services.InventoryCostRebuildService</c>. It loads the
/// ledger through <see cref="IInventoryCostLedgerStore"/>, replays it with
/// <see cref="WeightedAverageCostReplay"/> (issue #295) and decides what to persist: a real rebuild
/// stages every replayed movement's running position, the cost of each completed sale at or after
/// the requested recost date, and the product's position - but only when the history has no fatal
/// data-quality issue. A fatal issue stages nothing at all and throws
/// <see cref="InventoryCostDataQualityException"/> (issue #362), so a caller rebuilding several
/// products can keep the ones that succeeded. A dry run stages nothing and never throws for data
/// quality. No rounding is applied. Rebuilding twice over the same history yields the same result.
///
/// The ledger includes the product's costing repairs (issue #359), so a rebuild after a repair
/// recosts the completed sales the repaired history now supports and reports the fatal issues it
/// still does not. <see cref="RebuildCostingOnlyAsync"/> is that rebuild for a repair apply: the
/// same replay and the same decide-before-staging rule, staging the costing position and the sale
/// costs but neither the physical quantity nor any movement outcome, because a costing-only repair
/// has no authority over physical stock or the movement audit trail.
/// </summary>
public sealed class RebuildProductCost : IRebuildProductCost
{
    private readonly IInventoryCostLedgerStore _store;

    public RebuildProductCost(IInventoryCostLedgerStore store) => _store = store;

    public Task<InventoryCostRebuildResult> RebuildAsync(
        long productId,
        DateTime? recostCompletedSalesFrom = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RebuildAsync(productId, recostCompletedSalesFrom, dryRun, costingOnly: false, cancellationToken);

    public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
        long productId,
        DateTime recostCompletedSalesFrom,
        CancellationToken cancellationToken = default) =>
        RebuildAsync(productId, recostCompletedSalesFrom, dryRun: false, costingOnly: true, cancellationToken);

    private async Task<InventoryCostRebuildResult> RebuildAsync(
        long productId,
        DateTime? recostCompletedSalesFrom,
        bool dryRun,
        bool costingOnly,
        CancellationToken cancellationToken)
    {
        var ledger = await _store.LoadAsync(productId, forUpdate: !dryRun, cancellationToken)
            ?? throw new InvalidOperationException($"Product {productId} does not exist.");

        var replay = WeightedAverageCostReplay.Replay(
            ledger.Product, ledger.Adjustments, ledger.Sales, ledger.Repairs, ledger.Baseline);

        var recostedSaleCount = 0;
        if (!dryRun)
        {
            // Decide before staging anything: a fatal history must leave the caller's unit of work
            // completely untouched (issue #362), so a caller rebuilding a batch of products can save
            // the ones that replayed cleanly without carrying this product's partial replay with it.
            var fatal = replay.Issues.Where(x => x.IsFatal).Select(x => x.Message).Distinct().ToArray();
            if (fatal.Length > 0)
                throw new InventoryCostDataQualityException(string.Join(" ", fatal));

            var recostedSales = recostCompletedSalesFrom.HasValue
                ? replay.SaleCosts.Where(x => x.Sale.AuthorizationTime >= recostCompletedSalesFrom.Value).ToList()
                : [];
            // A costing-only rebuild stages the sale costs but no movement outcome and no physical
            // quantity, so the one thing it can change about physical stock is nothing (issue #359).
            _store.StageReplay(ledger, costingOnly ? [] : replay.Adjustments, recostedSales);
            recostedSaleCount = recostedSales.Count;

            _store.StageProductPosition(ledger, new ProductCostPosition(
                costingOnly ? null : replay.PhysicalQuantity,
                replay.CostingQuantity,
                replay.InventoryValue,
                replay.AverageUnitCost ?? 0m));
        }

        return new InventoryCostRebuildResult
        {
            ProductId = productId,
            PhysicalQuantity = replay.PhysicalQuantity,
            CostingQuantity = replay.CostingQuantity,
            InventoryValue = replay.InventoryValue,
            AverageUnitCost = replay.AverageUnitCost,
            RecostedSaleCount = recostedSaleCount,
            DryRun = dryRun,
            Issues = replay.Issues.Select(x => new InventoryCostDataQualityIssue(x.Code, x.Message)).ToList(),
        };
    }

    public async Task<decimal?> GetAverageUnitCostAtAsync(
        long productId,
        DateTime saleTime,
        long? saleTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        var ledger = await _store.LoadAsOfAsync(productId, saleTime, cancellationToken);
        if (ledger is null)
            return null;

        var replay = WeightedAverageCostReplay.Replay(
            ledger.Product, ledger.Adjustments, ledger.Sales, ledger.Repairs, ledger.Baseline, saleTransactionId, saleTime);
        return replay.HasFatalIssue ? null : replay.TargetSaleUnitCost ?? replay.AverageUnitCost;
    }
}
