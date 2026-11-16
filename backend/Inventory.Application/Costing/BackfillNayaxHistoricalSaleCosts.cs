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
///
/// <para><strong>The Nayax cost is precedence step 3, so a forced run fills only a sale no better
/// step has costed</strong> (issue #1261): every candidate passes
/// <see cref="SaleCostPrecedence.Replaces"/>, which leaves a ledger-costed (step 1) or
/// latest-purchase-costed (step 2) sale exactly as it is and rewrites another Nayax-costed sale only
/// when the export cost itself changed. Before that, <c>force</c> meant "overwrite", and this tool
/// could replace a sale's internal AVCO cost with the cost Nayax sent with the transaction.</para>
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
        // Filtered on the persisted sale-to-product association (issue #758), never on a match
        // re-made here: an unresolved sale belongs to no product and is in no product's backfill.
        var sales = (await _store.LoadStockConsumingSalesAsync(
                new StockConsumingSaleSelection(From: from, To: to), forUpdate: !dryRun, cancellationToken))
            .Where(s => !productId.HasValue || s.ProductId == productId.Value)
            .ToList();
        var withNayaxCost = sales.Count(s => s.NayaxProductCostPrice.HasValue);
        var invalid = sales.Count(s => s.NayaxProductCostPrice < 0);
        var alreadyCosted = sales.Count(s =>
            s.CostingStatus is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated);
        var candidates = sales.Where(s =>
            (force || s.CostingStatus is SaleCostStatus.Pending or SaleCostStatus.Error) &&
            s.NayaxProductCostPrice is >= 0 &&
            SaleCostPrecedence.Replaces(s, NayaxCost(s))).ToHashSet();

        var stillPending = sales.Count(s =>
            !candidates.Contains(s) &&
            s.CostingStatus is SaleCostStatus.Pending or SaleCostStatus.Error);
        var errorRows = sales.Count(s =>
            !candidates.Contains(s) && s.CostingStatus == SaleCostStatus.Error);

        if (!dryRun)
        {
            foreach (var sale in sales.Where(candidates.Contains))
                _store.StageCost(sale, NayaxCost(sale));
            await _store.SaveChangesAsync(cancellationToken);
        }

        return new NayaxCostBackfillResult(
            sales.Count, withNayaxCost, candidates.Count, alreadyCosted,
            stillPending, invalid, errorRows, dryRun);
    }

    /// <summary>The cost this tool would write onto <paramref name="sale"/>, and its provenance.</summary>
    private static SaleCostAssignment NayaxCost(CostableSale sale) =>
        new(sale.NayaxProductCostPrice, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport);
}
