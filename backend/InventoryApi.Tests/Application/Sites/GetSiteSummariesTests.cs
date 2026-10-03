using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Domain.Machines;
using InventoryApi.Adapters.Persistence;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Scoped-EF-context concurrency regression tests for <see cref="GetSiteSummaries"/> (issue #313).
/// Every <see cref="ISiteFactsStore"/> call in a request lands on one scoped <c>AppDbContext</c>, which
/// supports a single operation at a time, so the site dashboard must never have two of them in flight -
/// while the independent Nayax machine-product requests must stay a concurrent fan-out.
/// </summary>
public class GetSiteSummariesTests
{
    private const long AlphaSiteId = 43;
    private const long BravoSiteId = 42;

    private static readonly SiteProductActivityFact[] ActiveProducts =
    [
        new(ProductId: 1, IsActive: true),
        new(ProductId: 2, IsActive: true),
    ];

    /// <summary>
    /// Two sites, so the former per-site <c>Task.WhenAll</c> fan-out would start a second
    /// <c>GetRecentCompletedSalesAsync</c> while the first was still in flight. The recorder yields
    /// inside each call, so an overlapping implementation produces an interleaved trace and a
    /// concurrency count above one; the fixed implementation makes the catalogue read and then one
    /// batched completed-sales read for every site's machines, one at a time.
    /// </summary>
    [Fact]
    public async Task Handle_NeverOverlapsFactsStoreReads_AcrossSites()
    {
        var facts = new RecordingSiteFactsStore(ActiveProducts, Sales(DateTime.Now));
        var useCase = new GetSiteSummaries(Fleet(), facts, new SiteNameResolverAdapter());

        await useCase.Handle(CancellationToken.None);

        Assert.Equal(
            new[] { "start:activity", "end:activity", "start:sales", "end:sales" },
            facts.Trace);
        Assert.Equal(1, facts.MaxConcurrentCalls);

        var request = Assert.Single(facts.CompletedSalesRequests);
        Assert.Equal(new long[] { 10, 11, 20 }, request.MachineIds.Order());
    }

    /// <summary>
    /// The batched read must be distributed back to the site that owns each machine: every site stays in
    /// the result, in site-name order, with only its own machines' revenue.
    /// </summary>
    [Fact]
    public async Task Handle_AttributesEachMachinesRevenueToItsOwnSite()
    {
        var now = DateTime.Now;
        var facts = new RecordingSiteFactsStore(ActiveProducts, Sales(now));
        var useCase = new GetSiteSummaries(Fleet(), facts, new SiteNameResolverAdapter());

        var summaries = await useCase.Handle(CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Bravo" }, summaries.Select(summary => summary.SiteName));

        var alpha = summaries[0];
        Assert.Equal(AlphaSiteId, alpha.SiteId);
        Assert.Equal(1, alpha.MachineCount);
        Assert.Equal(9m, alpha.TodayRevenue);
        Assert.Equal(9m, alpha.CurrentWeekRevenue);
        Assert.Equal(3m, alpha.PreviousComparableWeekRevenue);
        Assert.Equal(100m, alpha.TotalStockPercentage);
        Assert.Equal(0, alpha.LowProductCount);
        Assert.Equal(0, alpha.EmptyProductCount);

        var bravo = summaries[1];
        Assert.Equal(BravoSiteId, bravo.SiteId);
        Assert.Equal(2, bravo.MachineCount);
        Assert.Equal(12m, bravo.TodayRevenue);
        Assert.Equal(12m, bravo.CurrentWeekRevenue);
        Assert.Equal(0m, bravo.PreviousComparableWeekRevenue);
        Assert.Equal(5m, bravo.TotalStockPercentage);
        Assert.Equal(1, bravo.LowProductCount);
        Assert.Equal(1, bravo.EmptyProductCount);
    }

    /// <summary>
    /// Nayax machine-product requests are independent remote reads that touch no scoped EF context, so
    /// serializing the database reads must not serialize them: both machines' requests are expected to be
    /// in flight at once. The gated client waits (bounded) until both have started, so a serialized
    /// implementation fails this assertion instead of hanging.
    /// </summary>
    [Fact]
    public async Task Handle_KeepsTheNayaxMachineProductFanOutConcurrent()
    {
        var nayax = new RecordingNayaxLynxClient(
            [
                new NayaxMachine { MachineID = 10, CustomerID = BravoSiteId, MachineName = "Bravo Left" },
                new NayaxMachine { MachineID = 20, CustomerID = AlphaSiteId, MachineName = "Alpha One" },
            ],
            expectedConcurrentMachineProductCalls: 2);
        var useCase = new GetSiteSummaries(nayax, new RecordingSiteFactsStore(ActiveProducts), new SiteNameResolverAdapter());

        var summaries = await useCase.Handle(CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(2, nayax.MaxConcurrentMachineProductCalls);
    }

    [Fact]
    public async Task Handle_PropagatesACompletedSalesReadFailure()
    {
        var facts = new RecordingSiteFactsStore(
            ActiveProducts, completedSalesFailure: new InvalidOperationException("sales read failed"));
        var useCase = new GetSiteSummaries(Fleet(), facts, new SiteNameResolverAdapter());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.Handle(CancellationToken.None));

        Assert.Equal("sales read failed", failure.Message);
    }

    /// <summary>
    /// The Nayax fan-out is started before the serialized database reads, so its failure must still
    /// surface instead of being dropped or reported as a complete result with a site missing.
    /// </summary>
    [Fact]
    public async Task Handle_PropagatesANayaxMachineProductFailure()
    {
        var useCase = new GetSiteSummaries(
            Fleet(failingMachineId: 11), new RecordingSiteFactsStore(ActiveProducts), new SiteNameResolverAdapter());

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.Handle(CancellationToken.None));
    }

    private static RecordingNayaxLynxClient Fleet(long? failingMachineId = null) =>
        new(
            [
                new NayaxMachine { MachineID = 10, CustomerID = BravoSiteId, MachineName = "Bravo Left" },
                new NayaxMachine { MachineID = 11, CustomerID = BravoSiteId, MachineName = "Bravo Right" },
                new NayaxMachine { MachineID = 20, CustomerID = AlphaSiteId, MachineName = "Alpha One" },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [10] =
                [
                    new() { MachineID = 10, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 4, VendOutAlertThreshold = 1 },
                    new() { MachineID = 10, NayaxProductID = 2, PAR = 5, MissingStockByMDB = 5, VendOutAlertThreshold = 1 },
                ],
                [11] =
                [
                    new() { MachineID = 11, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 5, VendOutAlertThreshold = 1 },
                    new() { MachineID = 11, NayaxProductID = 2, PAR = 5, MissingStockByMDB = 5, VendOutAlertThreshold = 1 },
                ],
                [20] =
                [
                    new() { MachineID = 20, NayaxProductID = 1, PAR = 10, MissingStockByMDB = 0, VendOutAlertThreshold = 2 },
                ],
            },
            failingMachineId: failingMachineId);

    /// <summary>
    /// Completed sales for both sites plus one machine outside the fleet, which must never be counted.
    /// The previous-comparable-week sale uses that period's own start boundary from the shared
    /// <see cref="MachineDashboardPeriods"/> definition rather than a hand-rolled offset.
    /// </summary>
    private static SiteCompletedSaleFact[] Sales(DateTime now) =>
    [
        new(MachineId: 10, SettlementValue: 5m, MachineAuthorizationTime: now),
        new(MachineId: 11, SettlementValue: 7m, MachineAuthorizationTime: now),
        new(MachineId: 20, SettlementValue: 9m, MachineAuthorizationTime: now),
        new(MachineId: 20, SettlementValue: 3m, MachineAuthorizationTime: MachineDashboardPeriods.PreviousComparableWeek(now).Start),
        new(MachineId: 99, SettlementValue: 1000m, MachineAuthorizationTime: now),
    ];
}
