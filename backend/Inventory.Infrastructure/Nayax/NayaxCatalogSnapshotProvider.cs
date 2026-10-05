using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Nayax;
using Inventory.Domain.CatalogReconciliation;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Implementation of <see cref="INayaxCatalogSnapshotProvider"/> backed by the Nayax Lynx client
/// (<see cref="INayaxLynxClient"/>, an Application-owned port implemented by
/// <see cref="NayaxLynxClient"/> since issue #49). Issue #306 moved this adapter here from
/// <c>InventoryApi.Adapters.Nayax</c>: it reads the remote catalogue and needs no
/// <c>AppDbContext</c>, so it is a real <c>Inventory.Infrastructure</c> resident and is registered
/// by <c>AddInfrastructureServices()</c> rather than directly in <c>Program.cs</c>. Its EF
/// counterpart, <c>EfLocalCatalogSnapshotProvider</c>, joined it here in issue #309
/// (Persistence 8/8 of #153).
///
/// A missing product/machine name is reported as an empty string rather than null: the Nayax
/// contract documents both <c>ProductName</c> (GET /v1/operators/{OperatorID}/products) and
/// <c>MachineName</c> (GET /v1/machines) as nullable strings, and the reconciliation policy always
/// has a remote name to compare or display, where an empty name is itself useful data-quality
/// information rather than an absent one.
/// </summary>
public sealed class NayaxCatalogSnapshotProvider : INayaxCatalogSnapshotProvider
{
    private readonly INayaxLynxClient _client;

    public NayaxCatalogSnapshotProvider(INayaxLynxClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<RemoteCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken)
    {
        var products = await _client.GetProductsAsync(cancellationToken);
        return products
            .Select(product => new RemoteCatalogEntry(product.NayaxProductId, product.ProductName ?? string.Empty))
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteCatalogEntry>> GetMachinesAsync(CancellationToken cancellationToken)
    {
        var machines = await _client.GetMachinesAsync(cancellationToken);
        return machines
            .Select(machine => new RemoteCatalogEntry(machine.MachineID, machine.MachineName ?? string.Empty))
            .ToList();
    }
}
