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
/// history is derived from the <c>NayaxSales</c> rows recorded against it: the <c>MachineName</c> on
/// its most recent sale is the latest reliable local name, and any other distinct name recorded for
/// the same <c>MachineID</c> is reported as a historical name for context. A rename is therefore
/// ordinary history, not a conflict. The current name is only ambiguous when the most recent
/// authorization time itself carries more than one distinct <c>MachineName</c>; that is the single
/// local signal the reconciliation policy treats as a conflicting identity.
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
                var latestTime = group.Max(row => row.MachineAuthorizationTime);
                var namesAtLatestTime = group
                    .Where(row => row.MachineAuthorizationTime == latestTime)
                    .Select(row => row.MachineName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                var currentName = namesAtLatestTime[0];
                var historicalNames = group
                    .OrderByDescending(row => row.MachineAuthorizationTime)
                    .ThenBy(row => row.MachineName, StringComparer.Ordinal)
                    .Select(row => row.MachineName!)
                    .Where(name => !string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new LocalCatalogEntry(group.Key, currentName, historicalNames, namesAtLatestTime.Count > 1);
            })
            .ToList();
    }
}
