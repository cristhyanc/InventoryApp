namespace Inventory.Application.Costing;

/// <summary>
/// Previews a costing-only historical repair for one product (issue #359) without persisting
/// anything at all - not even a draft.
///
/// The preview is where a repair is actually decided: it shows the costing position the repair
/// lands on, the value it adds, the resulting weighted-average unit cost, the first completed sale
/// the ledger cannot cost today, and where the whole history ends up afterwards, including the
/// fatal data-quality issues a partial repair would leave behind. It also reports the fingerprint
/// of the ledger it was computed from, which <see cref="ApplyInventoryCostRepair"/> requires back
/// so a repair cannot be applied against a history that changed in the meantime.
/// </summary>
public sealed class PreviewInventoryCostRepair
{
    private readonly InventoryCostRepairProjection _projection;

    public PreviewInventoryCostRepair(IInventoryCostRepairStore store, IInventoryCostLedgerStore ledgerStore) =>
        _projection = new InventoryCostRepairProjection(store, ledgerStore);

    public async Task<InventoryCostRepairPreview> Handle(
        InventoryCostRepairRequest request,
        CancellationToken cancellationToken = default) =>
        (await _projection.BuildAsync(request, cancellationToken)).Preview;
}
