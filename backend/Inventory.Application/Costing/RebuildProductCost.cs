using Inventory.Domain.Costing;

namespace Inventory.Application.Costing;

/// <summary>
/// Rebuilds a product's perpetual weighted-average cost (issue #296, child 2 of #149), moved
/// unchanged from the former <c>InventoryApi.Services.InventoryCostRebuildService</c>. It loads the
/// ledger through <see cref="IInventoryCostLedgerStore"/>, replays it with
/// <see cref="WeightedAverageCostReplay"/> (issue #295) and decides what to persist: on a real
/// rebuild every replayed movement's running position, the cost of each completed sale at or after
/// the requested recost date, and - only when the history has no fatal data-quality issue - the
/// product's position, otherwise <see cref="InventoryCostDataQualityException"/>. A dry run stages
/// nothing and never throws for data quality. No rounding is applied. Rebuilding twice over the
/// same history yields the same result.
/// </summary>
public sealed class RebuildProductCost : IRebuildProductCost
{
    private readonly IInventoryCostLedgerStore _store;

    public RebuildProductCost(IInventoryCostLedgerStore store) => _store = store;

    public async Task<InventoryCostRebuildResult> RebuildAsync(
        long productId,
        DateTime? recostCompletedSalesFrom = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var ledger = await _store.LoadAsync(productId, forUpdate: !dryRun, cancellationToken)
            ?? throw new InvalidOperationException($"Product {productId} does not exist.");

        var replay = WeightedAverageCostReplay.Replay(ledger.Product, ledger.Adjustments, ledger.Sales, ledger.Baseline);

        var recostedSaleCount = 0;
        if (!dryRun)
        {
            var recostedSales = recostCompletedSalesFrom.HasValue
                ? replay.SaleCosts.Where(x => x.Sale.AuthorizationTime >= recostCompletedSalesFrom.Value).ToList()
                : [];
            _store.StageReplay(ledger, replay.Adjustments, recostedSales);
            recostedSaleCount = recostedSales.Count;
        }

        var result = new InventoryCostRebuildResult
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

        if (dryRun)
            return result;

        var fatal = replay.Issues.Where(x => x.IsFatal).Select(x => x.Message).Distinct().ToArray();
        if (fatal.Length > 0)
            throw new InventoryCostDataQualityException(string.Join(" ", fatal));

        _store.StageProductPosition(ledger, new ProductCostPosition(
            replay.PhysicalQuantity,
            replay.CostingQuantity,
            replay.InventoryValue,
            replay.AverageUnitCost ?? 0m));

        return result;
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
            ledger.Product, ledger.Adjustments, ledger.Sales, ledger.Baseline, saleTransactionId, saleTime);
        return replay.HasFatalIssue ? null : replay.TargetSaleUnitCost ?? replay.AverageUnitCost;
    }
}
