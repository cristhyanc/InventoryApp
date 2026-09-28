using Inventory.Application.Nayax;

namespace Inventory.Application.SalesSync;

/// <summary>
/// The explicit latest-Nayax-sales synchronization use case (issue #187), extracted from the former
/// hidden <c>MachineService.SaveMachinesLastSalesAsync</c> side effect. It discovers the machines
/// through the <see cref="INayaxLynxClient"/> port, reads each machine's last sales through that same
/// port, and hands the whole batch to <see cref="ILatestNayaxSalesStore"/> to deduplicate, match,
/// classify, cost and persist, rebuilding inventory costs only for the products a completed sale
/// actually affected.
///
/// The home dashboard invokes it once before loading Sites and Machines, so both sections calculate
/// from the same freshness boundary; it never calculates machine or site figures itself.
/// </summary>
public sealed class SyncLatestNayaxSales
{
    private readonly INayaxLynxClient _nayax;
    private readonly ILatestNayaxSalesStore _store;

    public SyncLatestNayaxSales(INayaxLynxClient nayax, ILatestNayaxSalesStore store)
    {
        _nayax = nayax;
        _store = store;
    }

    public async Task Handle(CancellationToken cancellationToken = default)
    {
        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        // Every machine's latest sales are read before anything is persisted, so the batch is stored
        // in one save: a machine read that fails part-way through does not leave half a refresh
        // imported, exactly as the single save at the end of the former private method did.
        var sales = new List<NayaxLastSalesReport>();
        foreach (var machine in machines)
            sales.AddRange(await _nayax.GetMachineLastSalesAsync(machine.MachineID, cancellationToken));

        var persisted = await _store.PersistLatestSalesAsync(sales, cancellationToken);
        if (persisted.EarliestCompletedSaleByProductId.Count == 0)
            return;

        await _store.RebuildInventoryCostsAsync(
            persisted.EarliestCompletedSaleByProductId, cancellationToken);
    }
}
