using InventoryApi.DTOs;

namespace InventoryApi.Services.Interfaces;

/// <summary>
/// The machine-level Sync Restock workflow (issue #183): fetches new Nayax Event 501 stock-
/// adjustment alerts, persists them as imported facts, and previews their storage-inventory impact
/// before anything is applied.
/// </summary>
public interface INayaxMachineStockSyncService
{
    Task<NayaxMachineStockSyncPreviewDto> SyncAsync(long machineId, CancellationToken ct = default);

    Task<NayaxMachineStockApplyResponseDto> ApplyAsync(
        long machineId, IReadOnlyList<int> eventIds, CancellationToken ct = default);
}
