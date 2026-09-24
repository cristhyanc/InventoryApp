using Inventory.Application.CatalogReconciliation;
using Inventory.Domain.CatalogReconciliation;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ILocalCatalogSnapshotProvider"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>,
/// which still lives in InventoryApi. Move it into Inventory.Infrastructure once the shared
/// AppDbContext and persistence models relocate there.
///
/// Products are persisted directly, keyed by the Nayax product identifier (see
/// <c>ImportService.ImportProductsAsync</c>). Machines have no persisted entity at all - the current
/// remote-only machine view (<c>MachineService</c>) is not local history - so a machine's local
/// history is derived from the <c>NayaxSales</c> rows recorded against it: the most recent
/// <c>MachineName</c> is its current local name, and any other distinct name recorded for the same
/// <c>MachineID</c> is reported as a prior name so the reconciliation policy can flag the conflict.
/// </summary>
public sealed class EfLocalCatalogSnapshotProvider : ILocalCatalogSnapshotProvider
{
    private readonly AppDbContext _db;

    public EfLocalCatalogSnapshotProvider(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LocalCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken) =>
        await _db.Products
            .AsNoTracking()
            .Select(product => new LocalCatalogEntry(product.Id, product.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LocalCatalogEntry>> GetMachinesAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.NayaxSales
            .AsNoTracking()
            .Where(sale => sale.MachineName != null)
            .Select(sale => new { sale.MachineID, sale.MachineName, sale.MachineAuthorizationTime })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.MachineID)
            .Select(group =>
            {
                var latestName = group
                    .OrderByDescending(row => row.MachineAuthorizationTime)
                    .Select(row => row.MachineName!)
                    .First();
                var priorNames = group
                    .Select(row => row.MachineName!)
                    .Where(name => !string.Equals(name, latestName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new LocalCatalogEntry(group.Key, latestName, priorNames);
            })
            .ToList();
    }
}
