using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Costing;

/// <summary>
/// Recovers historical sale costs from the transaction-level Nayax <c>Product Cost Price</c>
/// already persisted on each sale by the Nayax transaction import (issue #297), moved unchanged
/// from the former <c>SaleCostingService.BackfillHistoricalCostsFromNayaxAsync</c> behind
/// <c>POST api/sale-costing/nayax-cost-backfill/dry-run</c> and <c>/apply</c>. It makes no Nayax
/// API call. A completed sale in the optional authorization-time range (and, optionally, matched to
/// one product) is a candidate when it is still pending or in error - or any status with
/// <c>force</c> - and has a non-negative Nayax cost; a candidate is costed at that cost with the
/// <see cref="SaleCostOrigin.NayaxTransactionExport"/> provenance. A negative Nayax cost is never
/// applied. A dry run loads untracked rows and stages and saves nothing.
/// </summary>
public sealed class BackfillNayaxHistoricalSaleCosts
{
    private readonly ISaleCostingStore _store;

    public BackfillNayaxHistoricalSaleCosts(ISaleCostingStore store) => _store = store;

    public async Task<NayaxCostBackfillResult> Handle(
        bool dryRun = true,
        bool force = false,
        DateTime? from = null,
        DateTime? to = null,
        long? productId = null,
        CancellationToken cancellationToken = default)
    {
        var products = await _store.GetProductCandidatesAsync(cancellationToken);
        var sales = (await _store.LoadCompletedSalesAsync(
                new CompletedSaleSelection(From: from, To: to), forUpdate: !dryRun, cancellationToken))
            .Where(s => !productId.HasValue ||
                ProductMatcher.Match(products, s.NayaxProductId, s.ProductName) == productId.Value)
            .ToList();
        var withNayaxCost = sales.Count(s => s.NayaxProductCostPrice.HasValue);
        var invalid = sales.Count(s => s.NayaxProductCostPrice < 0);
        var alreadyCosted = sales.Count(s =>
            s.CostingStatus is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated);
        var candidates = sales.Where(s =>
            (force || s.CostingStatus is SaleCostStatus.Pending or SaleCostStatus.Error) &&
            s.NayaxProductCostPrice is >= 0).ToHashSet();

        var stillPending = sales.Count(s =>
            !candidates.Contains(s) &&
            s.CostingStatus is SaleCostStatus.Pending or SaleCostStatus.Error);
        var errorRows = sales.Count(s =>
            !candidates.Contains(s) && s.CostingStatus == SaleCostStatus.Error);

        if (!dryRun)
        {
            foreach (var sale in sales.Where(candidates.Contains))
                _store.StageCost(sale, new SaleCostAssignment(
                    sale.NayaxProductCostPrice, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport));
            await _store.SaveChangesAsync(cancellationToken);
        }

        return new NayaxCostBackfillResult(
            sales.Count, withNayaxCost, candidates.Count, alreadyCosted,
            stillPending, invalid, errorRows, dryRun);
    }
}
