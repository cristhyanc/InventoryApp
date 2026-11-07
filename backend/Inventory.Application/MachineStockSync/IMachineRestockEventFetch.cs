using Inventory.Application.Machines;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// Fetches and imports one machine's new Nayax restock alerts (issue #1122): the import half of
/// Sync Restock, as <see cref="RefreshMachineRestockEvents"/> needs it. Implemented by
/// <see cref="SyncMachineStockFromNayax"/> itself, so the Dashboard's fetch and the operator's Sync
/// Restock are one import path - the same dedupe, classification, product mapping and #1018
/// coverage rule - and never two. It applies nothing.
/// </summary>
public interface IMachineRestockEventFetch
{
    Task FetchAsync(MachineIdentity machine, CancellationToken cancellationToken);
}
