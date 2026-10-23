namespace Inventory.Application.Sites;

/// <summary>
/// One site in the light site list (issue #841): the identifier a site filter submits and the name
/// it displays, and deliberately nothing else.
///
/// <para>It carries no stock percentage, alert count or revenue, because every one of those needs
/// the per-machine Nayax read the site pickers never show - see <see cref="ListSites"/>. A screen
/// that needs a site's stock or revenue reads <see cref="SiteSummary"/> through
/// <c>GET api/sites</c>, which is unchanged.</para>
///
/// <para>A <b>machine count</b> is a different case and is left out for a different reason: the one
/// fleet read this list is built from already holds every machine's site, so
/// <c>SiteSummary.MachineCount</c> is just the size of that grouping and would cost no extra Nayax
/// call here. It is absent because issue #841 fixed this row's published shape at the id and the
/// name; widening it is a contract decision for a human, not something the per-machine read
/// prevents.</para>
/// </summary>
public sealed record SiteListItem(long SiteId, string Name);
