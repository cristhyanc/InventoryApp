using Inventory.Domain.CatalogReconciliation;

namespace Inventory.Application.CatalogReconciliation;

/// <summary>
/// Narrow port for reading local product/machine history, owned by the Application layer. Products
/// are persisted directly; machines have no persisted entity, so their history is derived from
/// recorded Nayax sales (see the implementing adapter).
/// </summary>
public interface ILocalCatalogSnapshotProvider
{
    Task<IReadOnlyList<LocalCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalCatalogEntry>> GetMachinesAsync(CancellationToken cancellationToken);
}
