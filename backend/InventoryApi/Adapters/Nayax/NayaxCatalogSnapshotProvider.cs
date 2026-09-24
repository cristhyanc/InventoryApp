using Inventory.Application.CatalogReconciliation;
using Inventory.Domain.CatalogReconciliation;
using InventoryApi.Integrations.Nayax;

namespace InventoryApi.Adapters.Nayax;

/// <summary>
/// Implementation of <see cref="INayaxCatalogSnapshotProvider"/> backed by the Nayax Lynx client.
/// It lives in InventoryApi, not Inventory.Infrastructure, because <see cref="INayaxLynxClient"/>
/// still lives in InventoryApi (see <c>EfNayaxFeeRateStore</c> for the equivalent temporary-adapter
/// rationale on the persistence side).
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
