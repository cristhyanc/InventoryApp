using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// The low and empty product counts a site row publishes for each of its machines (issue #1019).
///
/// <para>They are <c>SiteStockPolicy.CalculateAlertCounts</c> - the site's own rule - over one
/// machine's selections instead of over every machine's at the site: the same exclusion of
/// selections with no linked or no active catalogue product, the same summing of several slots of
/// one product on one machine, and the same <c>MachineRefillAlertPolicy.Classify</c> test. Nothing
/// new is read and no definition of "low" or "empty" is added, so every case below asserts the
/// site's own counts beside the machine counts: a site counts a product once across its machines
/// over their combined stock, which deliberately need not equal the sum of its machines'
/// counts.</para>
///
/// <para>The per-machine stock percentage these counts sit beside, and the selections both are taken
/// from, are <c>SiteMachineStockTests</c> (issue #1013).</para>
/// </summary>
public class SiteMachineAlertCountTests
{
    private const long SiteId = 42;
    private const long LeftMachineId = 10;
    private const long RightMachineId = 11;

    private const long ChipsId = 1;
    private const long WaterId = 2;
    private const long BarsId = 3;

    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    private static readonly SiteProductActivityFact[] ActiveProducts =
    [
        new(ChipsId, IsActive: true),
        new(WaterId, IsActive: true),
        new(BarsId, IsActive: true),
    ];

    /// <summary>
    /// Two machines at one site holding different low and empty products: each row carries its own
    /// pair of counts, and the site keeps the counts it always published over the combined stock of
    /// each product across both machines.
    ///
    /// <para>Chips is the case that tells the two groupings apart: low on the left machine (2 left,
    /// alert threshold 3) and well stocked on the right (10 left), so the site classifies the
    /// combined 12 against the combined threshold of 6 and reports it stocked. The site's one low
    /// count is therefore <c>0</c> while its machines report one between them - the established rule
    /// this issue must not change.</para>
    /// </summary>
    [Fact]
    public async Task Handle_PublishesEachMachinesOwnAlertCounts()
    {
        var nayax = Fleet(
            (LeftMachineId,
                [
                    Selection(ChipsId, par: 10, missing: 8, threshold: 3),
                    Selection(WaterId, par: 5, missing: 5, threshold: 1),
                ]),
            (RightMachineId,
                [
                    Selection(ChipsId, par: 10, missing: 0, threshold: 3),
                    Selection(BarsId, par: 4, missing: 4, threshold: 1),
                ]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(
            new (long, int, int)[] { (LeftMachineId, 1, 1), (RightMachineId, 0, 1) },
            site.MachineStocks.Select(stock =>
                (stock.MachineId, stock.LowProductCount, stock.EmptyProductCount)));

        // The site's own counts: chips stocked over both machines, water and bars empty.
        Assert.Equal(0, site.LowProductCount);
        Assert.Equal(2, site.EmptyProductCount);
    }

    /// <summary>
    /// A product that is low on both machines is one alert on each machine and one alert at the
    /// site, because the site counts distinct products: the sum of the machine counts is 2 and the
    /// site's is 1.
    /// </summary>
    [Fact]
    public async Task Handle_CountsAProductLowOnTwoMachinesOncePerMachineAndOnceAtTheSite()
    {
        var nayax = Fleet(
            (LeftMachineId, [Selection(ChipsId, par: 10, missing: 9, threshold: 2)]),
            (RightMachineId, [Selection(ChipsId, par: 10, missing: 9, threshold: 2)]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(
            new (long, int, int)[] { (LeftMachineId, 1, 0), (RightMachineId, 1, 0) },
            site.MachineStocks.Select(stock =>
                (stock.MachineId, stock.LowProductCount, stock.EmptyProductCount)));

        // The combined 2 left is at or under the combined threshold of 4, so the site is low too -
        // once, for the one product, and not once per machine that holds it.
        Assert.Equal(1, site.LowProductCount);
        Assert.Equal(0, site.EmptyProductCount);
    }

    /// <summary>
    /// The counts cost no additional Nayax request: they come from the one cached selections read
    /// per machine the site percentage and the site counts already needed.
    /// </summary>
    [Fact]
    public async Task Handle_ReadsSelectionsOncePerMachineAndNoMore()
    {
        var nayax = Fleet(
            (LeftMachineId, [Selection(ChipsId, par: 10, missing: 9, threshold: 2)]),
            (RightMachineId, [Selection(ChipsId, par: 10, missing: 0, threshold: 2)]));

        await UseCase(nayax).Handle(CancellationToken.None);

        Assert.Equal(1, nayax.MachineListCalls);
        Assert.Equal(2, nayax.MachineProductCallCount);
    }

    /// <summary>
    /// A selection with no linked catalogue product, and one whose product is inactive, are not
    /// evaluated - the exclusion the site counts already apply - so neither invents a machine alert.
    /// Both still count toward the machine's physical stock percentage, exactly as they count toward
    /// the site's, which is why the percentage is asserted here too.
    /// </summary>
    [Fact]
    public async Task Handle_EvaluatesNeitherAnUnlinkedNorAnInactiveSelection()
    {
        var nayax = Fleet(
            (LeftMachineId,
                [
                    Selection(ChipsId, par: 10, missing: 10, threshold: 1),
                    // Inactive in the local catalogue.
                    Selection(WaterId, par: 10, missing: 10, threshold: 1),
                    // A Nayax product id this business holds no link for.
                    Selection(nayaxProductId: 99, par: 10, missing: 10, threshold: 1),
                    // A selection the provider reports no product for at all.
                    new NayaxMachineProduct { MachineID = LeftMachineId, PAR = 10, MissingStockByMDB = 10 },
                ]));

        var site = Assert.Single(
            await UseCase(
                    nayax,
                    productActivity: [new(ChipsId, IsActive: true), new(WaterId, IsActive: false)],
                    links: FakeNayaxProductLinkStore.Linking((ChipsId, ChipsId), (WaterId, WaterId)))
                .Handle(CancellationToken.None));

        var machine = Assert.Single(site.MachineStocks);
        Assert.Equal(0, machine.LowProductCount);
        Assert.Equal(1, machine.EmptyProductCount);
        Assert.Equal(1, site.EmptyProductCount);

        // The four selections are all empty of stock and all count toward capacity.
        Assert.Equal(0m, machine.StockPercentage);
        Assert.Equal(0m, site.TotalStockPercentage);
    }

    /// <summary>
    /// Several slots of one product on one machine are summed into a single selection before they
    /// are classified, the aggregation the Pick List and the site counts already apply. Two low
    /// slots of one product are one low product on that machine, and two empty slots of another are
    /// one empty product - never two of each.
    /// </summary>
    [Fact]
    public async Task Handle_SumsSeveralSlotsOfOneProductOnOneMachineFirst()
    {
        var nayax = Fleet(
            (LeftMachineId,
                [
                    Selection(ChipsId, par: 5, missing: 4, threshold: 1),
                    Selection(ChipsId, par: 5, missing: 4, threshold: 1),
                    Selection(WaterId, par: 3, missing: 3, threshold: 1),
                    Selection(WaterId, par: 3, missing: 3, threshold: 1),
                ]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        var machine = Assert.Single(site.MachineStocks);
        Assert.Equal(1, machine.LowProductCount);
        Assert.Equal(1, machine.EmptyProductCount);
        Assert.Equal(1, site.LowProductCount);
        Assert.Equal(1, site.EmptyProductCount);
    }

    /// <summary>
    /// A machine whose selections were read and hold nothing to evaluate reports <c>0</c> low and
    /// <c>0</c> empty: that is a fact about the machine, not a missing figure. A failed sites read
    /// publishes no site at all, so no row is ever handed a zero it did not earn.
    /// </summary>
    [Fact]
    public async Task Handle_PublishesZeroCountsForAMachineWithNothingToEvaluate()
    {
        var nayax = Fleet(
            (LeftMachineId, []),
            (RightMachineId, [Selection(ChipsId, par: 10, missing: 10, threshold: 1)]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(
            new (long, int, int)[] { (LeftMachineId, 0, 0), (RightMachineId, 0, 1) },
            site.MachineStocks.Select(stock =>
                (stock.MachineId, stock.LowProductCount, stock.EmptyProductCount)));
    }

    /// <summary>
    /// A selection the provider labelled with another machine's id is counted for the machine its
    /// request was made for, exactly as its stock percentage is (issue #1013), so an inconsistent
    /// echoed <c>MachineID</c> cannot move an alert onto a machine that does not hold it.
    /// </summary>
    [Fact]
    public async Task Handle_CountsSelectionsForTheMachineTheyWereRequestedFor()
    {
        var nayax = Fleet(
            (LeftMachineId, [Echoing(RightMachineId, ChipsId, par: 10, missing: 10, threshold: 1)]),
            (RightMachineId, [Echoing(LeftMachineId, ChipsId, par: 10, missing: 0, threshold: 1)]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(
            new (long, int, int)[] { (LeftMachineId, 0, 1), (RightMachineId, 0, 0) },
            site.MachineStocks.Select(stock =>
                (stock.MachineId, stock.LowProductCount, stock.EmptyProductCount)));
    }

    private static GetSiteSummaries UseCase(
        INayaxLynxClient nayax,
        IReadOnlyList<SiteProductActivityFact>? productActivity = null,
        INayaxProductLinkStore? links = null) =>
        new(
            nayax,
            new RecordingSiteFactsStore(productActivity ?? ActiveProducts),
            links ?? FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
            new SiteNameResolver(),
            Time.Clock,
            Time.Calendar,
            TestCurrentCapabilities.Owner);

    /// <summary>Machines standing at one site, each with the selections given for it.</summary>
    private static RecordingNayaxLynxClient Fleet(
        params (long MachineId, List<NayaxMachineProduct> Selections)[] machines) =>
        new(
            machines.Select(machine => new NayaxMachine
            {
                MachineID = machine.MachineId,
                CustomerID = SiteId,
                MachineName = $"Machine {machine.MachineId}",
            }),
            machines.ToDictionary(machine => machine.MachineId, machine => machine.Selections));

    /// <summary>One slot, labelled with no machine id, so the request decides whose it is.</summary>
    private static NayaxMachineProduct Selection(long nayaxProductId, int par, int missing, int threshold) =>
        new()
        {
            NayaxProductID = nayaxProductId,
            PAR = par,
            MissingStockByMDB = missing,
            VendOutAlertThreshold = threshold,
        };

    /// <summary>The same slot, labelled with a machine id the provider echoed back.</summary>
    private static NayaxMachineProduct Echoing(
        long echoedMachineId, long nayaxProductId, int par, int missing, int threshold)
    {
        var selection = Selection(nayaxProductId, par, missing, threshold);
        selection.MachineID = echoedMachineId;
        return selection;
    }
}
