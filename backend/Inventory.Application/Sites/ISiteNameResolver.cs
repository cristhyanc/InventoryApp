using Inventory.Application.Nayax;

namespace Inventory.Application.Sites;

/// <summary>
/// Narrow port wrapping the still-legacy <c>InventoryApi.Services.SiteNameResolver</c> (shared with
/// <c>GetSiteCommissionReport</c> and the transaction sales report, so it stays where it is rather than
/// moving with this slice; see <c>docs/architecture.md</c>). Nayax has no site name of its own, so the
/// implementation derives a display name from the site's machine names.
/// </summary>
public interface ISiteNameResolver
{
    string Resolve(IReadOnlyList<NayaxMachine> machines, long siteId);
}
