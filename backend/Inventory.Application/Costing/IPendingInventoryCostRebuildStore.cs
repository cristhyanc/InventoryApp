namespace Inventory.Application.Costing;

/// <summary>
/// Records that a product's inventory cost has to be replayed, without replaying it (issue #1304).
///
/// <para><strong>Why a port of its own.</strong> "What still has to be replayed is written down, not
/// remembered" (AGENTS.md § Inventory and historical costing invariants, issue #1103) - and every
/// writer that stores a sale has so far recorded that work inside its own adapter, because the sale
/// and the record of its replay must be one transaction. The sales history import of issue #1304 is
/// the first caller that stores sales through one adapter
/// (<see cref="Inventory.Application.Imports.INayaxSalesImportStore"/>) and needs the pending row
/// beside them, so the recording itself becomes a narrow port rather than a second copy of the rule
/// in a use case.</para>
///
/// <para><strong>It stages and does not save.</strong> The caller's own write-locked transaction and
/// its save are what make the sales, the costs and these rows one commit; an implementation that
/// saved here would commit a replay record for sales that are not committed yet.</para>
///
/// <para><strong>It replays nothing and decides nothing about cost.</strong> The rules are the
/// recorder's, unchanged: one row per <c>(business, product)</c>, an instant that only ever moves
/// earlier, and a revision that moves on at every enqueue so a replay already in flight cannot
/// acknowledge work it did not do. Which rows are then replayed, and whether a transition baseline
/// makes a row's work nothing at all, stays
/// <see cref="Inventory.Application.SalesSync.ILatestNayaxSalesStore.RebuildInventoryCostsAsync"/>'s
/// drain.</para>
/// </summary>
public interface IPendingInventoryCostRebuildStore
{
    /// <summary>
    /// Stages one pending rebuild per product in <paramref name="affected"/>, from that product's
    /// earliest affected instant. Saves nothing.
    /// </summary>
    /// <param name="affected">
    /// Each product a stock-consuming sale touched, mapped to the earliest such sale's instant.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task StageAsync(
        IReadOnlyDictionary<long, DateTime> affected, CancellationToken cancellationToken);
}
