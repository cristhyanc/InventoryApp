namespace Inventory.Application.Costing;

/// <summary>
/// Re-costs the business's completed sales (issue #297), moved unchanged from the former
/// <c>SaleCostingService.BackfillAsync</c> behind <c>POST api/sale-costing/backfill</c>. Without
/// <c>force</c> a sale already costed or legacy-estimated is counted as finalized and left
/// untouched; with it every completed sale is re-costed through <see cref="ICostSale"/>. A dry run
/// loads untracked rows and stages and saves nothing, so it only reports what a run would do;
/// repeating an applied run over the same history yields the same costs.
/// </summary>
public sealed class BackfillSaleCosts
{
    private readonly ISaleCostingStore _store;
    private readonly ICostSale _costSale;

    public BackfillSaleCosts(ISaleCostingStore store, ICostSale costSale)
    {
        _store = store;
        _costSale = costSale;
    }

    public async Task<SaleCostingBackfillResult> Handle(
        bool dryRun = true,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var sales = await _store.LoadCompletedSalesAsync(new CompletedSaleSelection(), forUpdate: !dryRun, cancellationToken);
        var costed = 0;
        var estimated = 0;
        var pending = 0;
        var errors = 0;
        var finalized = 0;

        foreach (var sale in sales)
        {
            if (!force && sale.CostingStatus is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated)
            {
                finalized++;
                continue;
            }

            var cost = await _costSale.Handle(sale, force, cancellationToken);
            if (cost is not null && !dryRun)
                _store.StageCost(sale, cost);
            switch (cost?.Status ?? sale.CostingStatus)
            {
                case SaleCostStatus.Costed: costed++; break;
                case SaleCostStatus.LegacyEstimated: estimated++; break;
                case SaleCostStatus.Pending: pending++; break;
                case SaleCostStatus.Error: errors++; break;
            }
        }

        if (!dryRun)
            await _store.SaveChangesAsync(cancellationToken);

        return new SaleCostingBackfillResult(costed, estimated, pending, errors, finalized, dryRun);
    }
}
