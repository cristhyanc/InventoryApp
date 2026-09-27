namespace Inventory.Application.Nayax;

/// <summary>
/// The application-facing port for the Nayax Lynx REST API (issue #49). Configuration-agnostic:
/// callers depend on this contract only, never on how a base URL, operator ID, or bearer token
/// reached the implementation. <c>Inventory.Infrastructure.Nayax.NayaxLynxClient</c> is the
/// concrete HTTP/configuration adapter.
/// Docs: https://devzone.nayax.com/docs/manage-data-operations/lynx-api
/// </summary>
public interface INayaxLynxClient
{
    Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default);
    Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default);
    Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default);
    Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default);
    Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default);
    Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default);
    Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default);
    Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default);

    /// <summary>
    /// The machine's last-reported alerts, including Event 501 ("Stock Adjust for Machine")
    /// stock-adjustment alerts consumed by the Sync Restock workflow (issue #183).
    /// Docs: https://devzone.nayax.com/reference/lynx/machines/get-machine-last-alerts
    /// </summary>
    Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default);
}
