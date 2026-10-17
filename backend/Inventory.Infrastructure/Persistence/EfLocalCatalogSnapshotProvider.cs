using Inventory.Application.CatalogReconciliation;
using Inventory.Domain.CatalogReconciliation;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ILocalCatalogSnapshotProvider"/>. It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> it depends on, which moved there in
/// issue #307; this adapter followed it in issue #309 (Persistence 8/8 of #153). Its remote
/// counterpart, <see cref="Inventory.Infrastructure.Nayax.NayaxCatalogSnapshotProvider"/>, arrived
/// with issue #306.
///
/// Products are persisted directly, and since issue #745 their Nayax identity is read from the
/// <c>ProductExternalIds</c> link table rather than from <c>Product.Id</c>. Machines have no
/// persisted entity at all - the current
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

    /// <summary>
    /// Every product this business has a Nayax identity for, reported under that identity (issue
    /// #745). The <c>ProductExternalIds</c> link is what a `NayaxProductID` names, so the local key
    /// is no longer read as one; the two reads are awaited one at a time, on the request's own
    /// scoped context, like every other local read here.
    ///
    /// <para>A local product with no Nayax link has no Nayax identity, so it is not part of this
    /// comparison at all: it is a product Nayax never named, not one Nayax stopped returning, and
    /// reporting it under its own key would be exactly the coincidence this resolution removes. A
    /// stored <c>ExternalId</c> the one Nayax conversion could not have written is skipped for the
    /// same reason - it is not a Nayax product id.</para>
    /// </summary>
    public async Task<IReadOnlyList<LocalCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken)
    {
        var links = await _db.ProductExternalIds
            .AsNoTracking()
            .Where(link => link.Provider == ExternalProvider.Nayax)
            .Select(link => new { link.ExternalId, link.ProductId })
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return [];

        var nameByProductId = await _db.Products
            .AsNoTracking()
            .Select(product => new { product.Id, product.Name })
            .ToDictionaryAsync(product => product.Id, product => product.Name, cancellationToken);

        var entries = new List<LocalCatalogEntry>(links.Count);
        foreach (var link in links)
        {
            // The link's composite foreign key guarantees the product exists in this business, so a
            // name that is missing here means the row was deleted between the two reads.
            if (NayaxExternalId.TryParse(link.ExternalId, out var nayaxProductId) &&
                nameByProductId.TryGetValue(link.ProductId, out var name))
                entries.Add(new LocalCatalogEntry(nayaxProductId, name));
        }

        return entries;
    }

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
