using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Sites;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// The light site list use case (issue #841). What it has to prove is mostly a <b>cost</b>: the site
/// pickers were filled from <see cref="GetSiteSummaries"/>, which reads the machine list and then
/// every machine's products to build figures a dropdown never shows, so the measurable criterion is
/// one <c>GetMachinesAsync</c> call and no <c>GetMachineProductsAsync</c> call at all. The rest is
/// that the list itself is the same list: one item per site, named by the shared
/// <see cref="ISiteNameResolver"/>, with a machine that stands at no site excluded exactly as
/// <c>GetSiteSummaries</c> excludes it.
///
/// <para>The real <see cref="SiteNameResolver"/> is used rather than a stub, because "names from
/// <c>SiteNameResolver</c>" is the criterion: a second naming rule here would let a site be called
/// one thing in the picker and another in the report it filters.</para>
/// </summary>
public class ListSitesTests
{
    private const long AlphaSiteId = 43;
    private const long BravoSiteId = 42;

    /// <summary>
    /// The acceptance criterion: N sites from one fleet read, and not one per-machine read - the
    /// 1 + M calls <c>GetSiteSummaries</c> makes for the same dropdown.
    /// </summary>
    [Fact]
    public async Task Handle_ReadsTheMachineListOnce_AndNoMachineProducts()
    {
        var nayax = Fleet();

        var sites = await UseCase(nayax).Handle(CancellationToken.None);

        Assert.Equal(2, sites.Count);
        Assert.Equal(1, nayax.MachineListCalls);
        Assert.Equal(0, nayax.MachineProductCalls);
    }

    /// <summary>
    /// One item per site, carrying the id a filter submits and the name
    /// <see cref="SiteNameResolver"/> derives from the site's machine names - the first token of its
    /// first named machine. The order is by name, as the site dashboard orders its rows, so a
    /// picker that renders the list as it arrives is unchanged.
    /// </summary>
    [Fact]
    public async Task Handle_ReturnsOneSitePerCustomerId_NamedByTheSharedResolver()
    {
        var sites = await UseCase(Fleet()).Handle(CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Bravo" }, sites.Select(site => site.Name));
        Assert.Equal(new[] { AlphaSiteId, BravoSiteId }, sites.Select(site => site.SiteId));
    }

    /// <summary>
    /// A machine Nayax returns with no <c>CustomerID</c> stands at no site: it is excluded, and it
    /// does not become a site of its own or an unnamed entry. The site it would have polluted keeps
    /// its own name.
    /// </summary>
    [Fact]
    public async Task Handle_ExcludesAMachineWithNoCustomerId()
    {
        var nayax = new RecordingNayaxLynxClient(
        [
            new NayaxMachine { MachineID = 10, CustomerID = BravoSiteId, MachineName = "Bravo Left" },
            new NayaxMachine { MachineID = 99, CustomerID = null, MachineName = "Unassigned Spare" },
        ]);

        var sites = await UseCase(nayax).Handle(CancellationToken.None);

        var site = Assert.Single(sites);
        Assert.Equal(BravoSiteId, site.SiteId);
        Assert.Equal("Bravo", site.Name);
    }

    /// <summary>
    /// A site whose every machine is unnamed still has an identity, through the resolver's
    /// <c>Site {id}</c> fallback: a picker entry with an empty label would be unselectable.
    /// </summary>
    [Fact]
    public async Task Handle_FallsBackToTheResolversSiteNumber_WhenNoMachineIsNamed()
    {
        var nayax = new RecordingNayaxLynxClient(
            [new NayaxMachine { MachineID = 10, CustomerID = BravoSiteId, MachineName = null }]);

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal($"Site {BravoSiteId}", site.Name);
    }

    /// <summary>
    /// A fleet read that answers with no site-mapped machine is an empty list, which is a real
    /// answer: this business's fleet stands at no site Nayax has mapped. It is reached only when the
    /// read succeeded - the two failures below are what an unusable provider produces.
    /// </summary>
    [Fact]
    public async Task Handle_ReturnsNoSite_WhenTheFleetHasNoSiteMappedMachine()
    {
        var nayax = new RecordingNayaxLynxClient(
            [new NayaxMachine { MachineID = 10, CustomerID = null, MachineName = "Unassigned Spare" }]);

        Assert.Empty(await UseCase(nayax).Handle(CancellationToken.None));
    }

    /// <summary>
    /// The unavailable states are the shared ones and are not swallowed: a business whose Nayax
    /// connection the status gate refuses, and a Nayax account that has not granted the fleet read,
    /// propagate from the one call this use case makes, so the endpoint answers the same <c>409</c>
    /// codes <c>GET api/sites</c> does and the picker's <c>loadProviderList</c> already handles.
    /// Reporting either of them as a business with no sites is the one thing a site filter must
    /// never do.
    /// </summary>
    [Fact]
    public async Task Handle_PropagatesTheNotConnectedRefusal()
    {
        var nayax = Fleet(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured));

        var failure = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(NayaxConnectionStatus.NotConfigured, failure.Status);
    }

    [Fact]
    public async Task Handle_PropagatesThePermissionRefusal()
    {
        var nayax = Fleet(new NayaxPermissionNotGrantedException("GetMachinesAsync"));

        await Assert.ThrowsAsync<NayaxPermissionNotGrantedException>(
            () => UseCase(nayax).Handle(CancellationToken.None));
    }

    private static ListSites UseCase(INayaxLynxClient nayax) => new(nayax, new SiteNameResolver());

    /// <summary>
    /// Three machines at two sites, with per-machine products available for every one of them - so a
    /// use case that read them would succeed, and the call-count assertion above is about a choice
    /// rather than about missing fixtures.
    /// </summary>
    private static RecordingNayaxLynxClient Fleet(Exception? machineListFailure = null) =>
        new(
            [
                new NayaxMachine { MachineID = 10, CustomerID = BravoSiteId, MachineName = "Bravo Left" },
                new NayaxMachine { MachineID = 11, CustomerID = BravoSiteId, MachineName = "Bravo Right" },
                new NayaxMachine { MachineID = 20, CustomerID = AlphaSiteId, MachineName = "Alpha One" },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [10] = [new() { MachineID = 10, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 1 }],
                [11] = [new() { MachineID = 11, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 1 }],
                [20] = [new() { MachineID = 20, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 1 }],
            },
            machineListFailure: machineListFailure);
}
