using Inventory.Application.Nayax;

namespace Inventory.Application.Sites;

/// <summary>
/// The light site list use case (issue #841): every site the business's live Nayax fleet stands at,
/// as an id and a display name, from <b>one</b> <see cref="INayaxLynxClient.GetMachinesAsync"/> call
/// and no per-machine read at all.
///
/// <para>It exists because a site picker needs nothing else. The site filters on the Site
/// commissions report, the Site commission agreements page and the Site Products page were filled
/// from <see cref="GetSiteSummaries"/>, which reads the machine list and then
/// <see cref="INayaxLynxClient.GetMachineProductsAsync"/> for every machine in the fleet - 1 + M
/// Nayax calls - to build the stock, alert and revenue figures a dropdown never shows. A site's id
/// and name come from the machine list alone: Nayax identifies a site by <c>CustomerID</c> and
/// publishes no site name, so the name is derived from the site's machine names by
/// <see cref="ISiteNameResolver"/>, the same resolver <c>GetSiteSummaries</c> and
/// <c>GetSiteCommissionReport</c> use, so no two screens can name one site differently.</para>
///
/// <para>A machine with no <c>CustomerID</c> stands at no site and is excluded, exactly as it is in
/// <see cref="GetSiteSummaries"/>. The result is ordered by name for the same reason that use case
/// orders its rows: the pickers present the list as it arrives.</para>
///
/// <para>It reads no capability and no local data. There is no financial figure here to withhold
/// (issue #697 is about the revenue <see cref="SiteSummary"/> carries), and nothing on this path
/// touches the database, so the endpoint's own <c>Inventory.Operate</c> capability is the whole
/// access decision. The provider states are the shared ones: a business whose connection the status
/// gate refuses, or whose Nayax account has not granted the fleet read, fails inside the credential
/// provider or the client exactly as <c>GET api/sites</c> does, and those failures propagate from
/// here unchanged - this use case answers no "available/unavailable" shape of its own and reports no
/// empty site list in their place.</para>
/// </summary>
public sealed class ListSites
{
    private readonly INayaxLynxClient _nayax;
    private readonly ISiteNameResolver _siteNames;

    public ListSites(INayaxLynxClient nayax, ISiteNameResolver siteNames)
    {
        _nayax = nayax;
        _siteNames = siteNames;
    }

    public async Task<IReadOnlyList<SiteListItem>> Handle(CancellationToken cancellationToken) =>
        (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID.HasValue)
            .GroupBy(machine => machine.CustomerID!.Value)
            .Select(site => new SiteListItem(site.Key, _siteNames.Resolve(site.ToList(), site.Key)))
            .OrderBy(site => site.Name)
            .ToList();
}
