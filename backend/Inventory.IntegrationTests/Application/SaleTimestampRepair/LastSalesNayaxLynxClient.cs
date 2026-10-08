using Inventory.Application.Nayax;

namespace InventoryApi.Tests.Application.SaleTimestampRepair;

/// <summary>
/// Fake <see cref="INayaxLynxClient"/> serving a fixed rolling last-sales window per machine, for
/// the sale timestamp repair tests (issue #472). Every other member throws, because the repair reads
/// exactly two endpoints - the machine fleet and each machine's last sales - and a test that
/// accidentally depended on a third should fail loudly rather than quietly.
/// </summary>
public sealed class LastSalesNayaxLynxClient : INayaxLynxClient
{
    private readonly IReadOnlyDictionary<long, List<NayaxLastSalesReport>> _salesByMachine;

    public LastSalesNayaxLynxClient(IReadOnlyDictionary<long, List<NayaxLastSalesReport>> salesByMachine) =>
        _salesByMachine = salesByMachine;

    /// <summary>An empty window: the live source returns nothing for any machine.</summary>
    public static LastSalesNayaxLynxClient Empty(params long[] machineIds) =>
        new(machineIds.ToDictionary(id => id, _ => new List<NayaxLastSalesReport>()));

    public int MachineLastSalesCalls { get; private set; }

    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
        Task.FromResult(_salesByMachine.Keys
            .OrderBy(id => id)
            .Select(id => new NayaxMachine { MachineID = id, MachineName = $"Machine {id}" })
            .ToList());

    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default)
    {
        MachineLastSalesCalls++;
        return Task.FromResult(_salesByMachine.TryGetValue(machineId, out var sales) ? sales : []);
    }

    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
}
