using Inventory.Application.Nayax;

namespace Inventory.Application.Sites;

/// <summary>
/// Narrow port for a site's display name, shared by <c>GetSiteSummaries</c> and
/// <c>GetSiteCommissionReport</c>. Nayax has no site name of its own, so the implementation
/// (<c>Inventory.Infrastructure.Sites.SiteNameResolver</c> since issue #306) derives one from the
/// site's machine names; see <c>docs/architecture.md</c>.
/// </summary>
public interface ISiteNameResolver
{
    string Resolve(IReadOnlyList<NayaxMachine> machines, long siteId);
}
