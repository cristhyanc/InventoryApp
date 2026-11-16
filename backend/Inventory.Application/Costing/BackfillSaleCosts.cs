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
/// <see cref="SaleCostPrecedence.Replaces(CostableSale, SaleCostAssignment)"/> before it is staged,
/// so a better-sourced stored cost is
/// never replaced by a worse-sourced one and an equal-sourced one is rewritten only when the value
/// differs. A sale nothing is written for is reported by the state it is already in - finalized when
/// a final cost stood, pending or in error when the run left it exactly as it found it - and a
/// repeated forced run therefore stages nothing and asks for no cost rebuild.</para>
///
/// <para>The decisions are made on sales read before the store takes its write lock, so each staged
/// cost is asked again inside that lock (<see cref="SaleCostPrecedence.StillReplaces"/>): a cost
/// another writer committed in that gap is not written over, records no rebuild, and is counted by
/// the state the sale is now in rather than as one this run wrote.</para>
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
        var counts = new OutcomeCounts();

        foreach (var sale in sales)
        {
            if (!force && IsFinal(sale.CostingStatus))
            {
                counts.RecordFinalized();
                continue;
            }

            var cost = await _costSale.Handle(
                sale, force, SaleCostPrecedenceScope.Complete, cancellationToken);
            if (cost is null || !SaleCostPrecedence.Replaces(sale, cost))
            {
                counts.RecordUnchanged(sale.CostingStatus);
                continue;
            }

            if (!dryRun)
                _store.StageCost(sale, cost);
            counts.RecordDecided(cost.Status);
        }

        if (!dryRun)
            counts.Reclassify((await _store.SaveChangesAsync(cancellationToken)).Refused);

        return counts.ToResult(dryRun);
    }

    /// <summary>Whether a stored costing status is a final cost this tool reports as finalized.</summary>
    private static bool IsFinal(SaleCostStatus status) =>
        status is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated;

    /// <summary>
    /// The run's counts, so each sale is recorded in exactly one place: by the cost the run decided
    /// for it, or - when nothing was written - by the state it is already in.
    /// </summary>
    private sealed class OutcomeCounts
    {
        private int _costed;
        private int _estimated;
        private int _pending;
        private int _errors;
        private int _finalized;

        /// <summary>Records a sale that already had a final cost an unforced run leaves alone.</summary>
        public void RecordFinalized() => _finalized++;

        /// <summary>Records a sale by the status the run decided for it.</summary>
        public void RecordDecided(SaleCostStatus status) => Count(status, 1);

        /// <summary>
        /// Takes back each cost the write lock refused and records that sale by the state the latest
        /// persisted row holds instead (issue #1261): the run wrote nothing for it and recorded no
        /// rebuild for it, so it did not cost it.
        /// </summary>
        public void Reclassify(IEnumerable<RefusedSaleCost> refused)
        {
            foreach (var cost in refused)
            {
                Count(cost.Decided.Status, -1);
                RecordUnchanged(cost.Stored.Status);
            }
        }

        /// <summary>
        /// Records a sale nothing was written for by the state it is already in: a final cost of at
        /// least the decision's precedence is finalized, and a sale the run left exactly as it found
        /// it is still pending or in error.
        /// </summary>
        public void RecordUnchanged(SaleCostStatus stored)
        {
            if (IsFinal(stored))
                RecordFinalized();
            else
                RecordDecided(stored);
        }

        /// <summary>Moves the counter for <paramref name="status"/> by <paramref name="delta"/>.</summary>
        private void Count(SaleCostStatus status, int delta)
        {
            switch (status)
            {
                case SaleCostStatus.Costed: _costed += delta; break;
                case SaleCostStatus.LegacyEstimated: _estimated += delta; break;
                case SaleCostStatus.Error: _errors += delta; break;
                default: _pending += delta; break;
            }
        }

        public SaleCostingBackfillResult ToResult(bool dryRun) =>
            new(_costed, _estimated, _pending, _errors, _finalized, dryRun);
    }
}
