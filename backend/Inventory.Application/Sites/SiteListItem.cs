namespace Inventory.Application.Sites;

/// <summary>
/// One site in the light site list (issue #841): the identifier a site filter submits, the name it
/// displays, and how many machines the business's fleet has there.
///
/// <para>It carries no stock percentage, alert count or revenue, because every one of those needs
/// the per-machine Nayax read the site pickers never show - see <see cref="ListSites"/>. A screen
/// that needs a site's stock or revenue reads <see cref="SiteSummary"/> through
/// <c>GET api/sites</c>, which is unchanged.</para>
///
/// <para><see cref="MachineCount"/> is here because it costs nothing to publish and the Site
/// Products page states it: the one fleet read this list is built from already holds every machine's
/// site, so the count is the size of that <c>CustomerID</c> grouping - the same
/// <c>machines.Count</c> <see cref="SiteSummary.MachineCount"/> reports - and no second Nayax call,
/// and in particular no per-machine product read, is made to obtain it. It counts every machine in
/// the group, including one with no mapped products, so it is never derived from product rows. The
/// repository owner approved widening this row to carry it on 23 October 2026; a consumer that
/// needs only the id and the name ignores it.</para>
/// </summary>
public sealed record SiteListItem(long SiteId, string Name, int MachineCount);
