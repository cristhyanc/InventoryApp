using Inventory.Application.Nayax;
using Inventory.Application.Sites;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Thin adapter delegating to the still-legacy <c>InventoryApi.Services.SiteNameResolver</c>, shared
/// with <c>GetSiteCommissionReport</c> and the transaction sales report (see <c>docs/architecture.md</c>).
/// </summary>
public sealed class SiteNameResolverAdapter : ISiteNameResolver
{
    public string Resolve(IReadOnlyList<NayaxMachine> machines, long siteId) =>
        InventoryApi.Services.SiteNameResolver.FromMachines(machines, siteId);
}
