namespace Inventory.Application.Sites;

/// <summary>
/// One site in the light site list (issue #841): the identifier a site filter submits and the name
/// it displays, and deliberately nothing else.
///
/// <para>It carries no machine count, stock percentage, alert count or revenue, because every one
/// of those needs the per-machine Nayax read the site pickers never show - see
/// <see cref="ListSites"/>. A screen that needs a site's stock or revenue reads
/// <see cref="SiteSummary"/> through <c>GET api/sites</c>, which is unchanged.</para>
/// </summary>
public sealed record SiteListItem(long SiteId, string Name);
