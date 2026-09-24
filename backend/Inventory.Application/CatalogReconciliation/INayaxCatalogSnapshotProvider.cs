using Inventory.Domain.CatalogReconciliation;

namespace Inventory.Application.CatalogReconciliation;

/// <summary>
/// Narrow port for reading Nayax's current product/machine identities, owned by the Application
/// layer. Its implementation talks to Nayax; nothing above this port knows how.
/// </summary>
public interface INayaxCatalogSnapshotProvider
{
    Task<IReadOnlyList<RemoteCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteCatalogEntry>> GetMachinesAsync(CancellationToken cancellationToken);
}
