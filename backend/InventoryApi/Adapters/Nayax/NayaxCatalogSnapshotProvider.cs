using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Nayax;
using Inventory.Domain.CatalogReconciliation;

namespace InventoryApi.Adapters.Nayax;

/// <summary>
/// Implementation of <see cref="INayaxCatalogSnapshotProvider"/> backed by the Nayax Lynx client
/// (<see cref="INayaxLynxClient"/>, an Application-owned port implemented by
/// <c>Inventory.Infrastructure.Nayax.NayaxLynxClient</c> since issue #49). This adapter itself
/// still lives in InventoryApi rather than Inventory.Infrastructure; relocating it is outside
/// issue #49's scope.
///
/// A missing product/machine name is reported as an empty string rather than null: the reconciliation
/// policy always has a remote name to compare or display, and an empty name is itself useful
/// data-quality information rather than an absent one.
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
