namespace InventoryApi.Services.Interfaces;

/// <summary>
/// Synchronizes the latest Nayax sales transactions into persisted <c>NayaxSales</c> rows. This is
/// the explicit, shared latest-sales import extracted from the former
/// <c>MachineService.SaveMachinesLastSalesAsync</c> so any caller that needs a fresh sales baseline
/// (the coordinated home-dashboard refresh) can invoke it once, rather than relying on it as a
/// side effect of loading machine data.
/// </summary>
public interface INayaxLatestSalesSyncService
{
    Task SyncLatestSalesAsync(CancellationToken ct = default);
}
