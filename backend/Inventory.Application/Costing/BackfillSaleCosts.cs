namespace Inventory.Application.Costing;

/// <summary>
/// Re-costs the business's completed sales (issue #297), moved unchanged from the former
/// <c>SaleCostingService.BackfillAsync</c> behind <c>POST api/sale-costing/backfill</c>. Without
/// <c>force</c> a sale already costed or legacy-estimated is counted as finalized and left
/// untouched; with it every completed sale is re-costed through <see cref="ICostSale"/>. A dry run
/// loads untracked rows and stages and saves nothing, so it only reports what a run would do;
/// repeating an applied run over the same history yields the same costs.
///
/// <para><strong>It re-costs through the complete four-step precedence, and it may only improve a
/// sale's cost</strong> (issue #1261). Two things make that true. It asks
/// <see cref="ICostSale"/> for <see cref="SaleCostPrecedenceScope.Complete"/>, so a sale the ledger
/// cannot cost takes the latest costed purchase before it (step 2) rather than falling through to
/// its own Nayax cost (step 3) - the step the sale-time path leaves to the rebuild that follows it,
/// which a backfill does not have. And every decision passes
/// <see cref="SaleCostPrecedence.Replaces"/> before it is staged, so a better-sourced stored cost is
/// never replaced by a worse-sourced one and an equal-sourced one is rewritten only when the value
/// differs. A sale nothing is written for is reported by the state it is already in - finalized when
/// a final cost stood, pending or in error when the run left it exactly as it found it - and a
/// repeated forced run therefore stages nothing and asks for no cost rebuild.</para>
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
        var sales = await _store.LoadStockConsumingSalesAsync(
            new StockConsumingSaleSelection(), forUpdate: !dryRun, cancellationToken);
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

            var cost = await _costSale.Handle(
                sale, force, SaleCostPrecedenceScope.Complete, cancellationToken);
            if (cost is null || !SaleCostPrecedence.Replaces(sale, cost))
            {
                // Nothing to write, so the sale is reported by the state it is already in: a final
                // cost of at least this decision's precedence is finalized, and a sale this run left
                // exactly as it found it is still pending or in error.
                if (sale.CostingStatus is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated)
                    finalized++;
                else if (sale.CostingStatus == SaleCostStatus.Error)
                    errors++;
                else
                    pending++;
                continue;
            }

            if (!dryRun)
                _store.StageCost(sale, cost);
            switch (cost.Status)
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
