using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// The per-machine stock percentage a site row publishes for each of its machines (issue #1013).
///
/// <para>The split is taken from selections <see cref="GetSiteSummaries"/> already fetches for the
/// site percentage - one cached <c>GetMachineProductsAsync</c> per machine, which is why these cases
/// assert the machine-product call count as well as the figures. Nothing new is read, nothing is
/// derived from the site figure, and the site figure itself is asserted unchanged beside every
/// machine figure, because the two are the same rule over different groupings and an agreement that
/// only holds by coincidence is not one.</para>
///
/// <para>Each machine's figure is attributed to the machine the request was <em>made for</em> rather
/// than to the <c>MachineID</c> the provider echoed back on a selection, so a selection carrying the
/// wrong machine id - or none - cannot move a figure onto another machine's row.</para>
///
/// <para>That the figures are identical for a role without <c>Dashboard.ViewFinancials</c> - stock
/// is not money, and issue #697's redaction removes amounts and nothing else - is asserted where
/// that decision lives, in <c>SiteFinancialRedactionTests</c>.</para>
/// </summary>
public class SiteMachineStockTests
{
    private const long SiteId = 42;
    private const long OtherSiteId = 43;
    private const long FullMachineId = 10;
    private const long HalfMachineId = 11;
    private const long OtherSiteMachineId = 20;

    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    private static readonly SiteProductActivityFact[] ActiveProducts = [new(ProductId: 1, IsActive: true)];

    /// <summary>
    /// Two machines at one site with different stock: each row carries its own percentage, and the
    /// site keeps the single figure it always published over both machines' selections together.
    /// </summary>
    [Fact]
    public async Task Handle_PublishesEachMachinesOwnStockPercentage()
    {
        var nayax = Fleet(
            (FullMachineId, [Selection(FullMachineId, par: 10, missing: 0)]),
            (HalfMachineId, [Selection(HalfMachineId, par: 10, missing: 7)]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(65m, site.TotalStockPercentage);
        Assert.Equal(
            new (long, decimal?)[] { (FullMachineId, 100m), (HalfMachineId, 30m) },
            site.MachineStocks.Select(stock => (stock.MachineId, stock.StockPercentage)));
    }

    /// <summary>
    /// The figures cost no extra Nayax request: the one cached selections read per machine the site
    /// percentage already needed is the whole of what they are taken from.
    /// </summary>
    [Fact]
    public async Task Handle_ReadsSelectionsOncePerMachineAndNoMore()
    {
        var nayax = Fleet(
            (FullMachineId, [Selection(FullMachineId, par: 10, missing: 0)]),
            (HalfMachineId, [Selection(HalfMachineId, par: 10, missing: 7)]));

        await UseCase(nayax).Handle(CancellationToken.None);

        Assert.Equal(1, nayax.MachineListCalls);
        Assert.Equal(2, nayax.MachineProductCallCount);
    }

    /// <summary>
    /// A machine whose selections carry no capacity at all reports no percentage - not 0%, and not
    /// the 100% the <em>site</em> rule answers for a site with no capacity, which stays as it is.
    /// </summary>
    [Fact]
    public async Task Handle_PublishesNoPercentageForAMachineWithNoCapacity()
    {
        var nayax = Fleet(
            (FullMachineId, [Selection(FullMachineId, par: 0, missing: 0)]),
            (HalfMachineId, []));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(100m, site.TotalStockPercentage);
        Assert.All(site.MachineStocks, stock => Assert.Null(stock.StockPercentage));
        Assert.Equal(
            new[] { FullMachineId, HalfMachineId },
            site.MachineStocks.Select(stock => stock.MachineId));
    }

    /// <summary>
    /// A missing <c>PAR</c> or <c>MissingStockByMDB</c> is treated exactly as the site percentage
    /// treats it today - absent capacity is no capacity, absent missing stock is nothing missing -
    /// so a machine figure and the site figure above it cannot disagree about an incomplete
    /// selection. The first machine's one selection reports no missing stock at all and is therefore
    /// full; the second holds a selection with missing stock the provider gave no capacity for,
    /// which the arithmetic subtracts without adding capacity for, exactly as the site figure does.
    /// </summary>
    [Fact]
    public async Task Handle_TreatsMissingSelectionValuesAsTheSitePercentageDoes()
    {
        var nayax = Fleet(
            (FullMachineId, [new NayaxMachineProduct { MachineID = FullMachineId, NayaxProductID = 1, PAR = 10 }]),
            (HalfMachineId,
                [
                    Selection(HalfMachineId, par: 10, missing: 5),
                    new NayaxMachineProduct { MachineID = HalfMachineId, NayaxProductID = 1, MissingStockByMDB = 3 },
                ]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        // Site: (10 - 0) + (10 - 5) + (0 - 3) in stock over 10 + 10 + 0 of capacity.
        Assert.Equal(60m, site.TotalStockPercentage);
        Assert.Equal(
            new (long, decimal?)[] { (FullMachineId, 100m), (HalfMachineId, 20m) },
            site.MachineStocks.Select(stock => (stock.MachineId, stock.StockPercentage)));
    }

    /// <summary>
    /// A selection the provider labelled with another machine's id belongs to the machine it was
    /// requested for. The requested machine is what the row is keyed by, so an inconsistent echoed
    /// <c>MachineID</c> cannot attribute stock to a machine that does not hold it.
    /// </summary>
    [Fact]
    public async Task Handle_AttributesSelectionsToTheMachineTheyWereRequestedFor()
    {
        var nayax = Fleet(
            (FullMachineId, [Selection(HalfMachineId, par: 10, missing: 0)]),
            (HalfMachineId, [Selection(FullMachineId, par: 10, missing: 10)]));

        var site = Assert.Single(await UseCase(nayax).Handle(CancellationToken.None));

        Assert.Equal(
            new (long, decimal?)[] { (FullMachineId, 100m), (HalfMachineId, 0m) },
            site.MachineStocks.Select(stock => (stock.MachineId, stock.StockPercentage)));
    }

    /// <summary>
    /// Each site publishes its own machines and no others, in the order the fleet read returned
    /// them, so a second site cannot contribute a row - or a figure - to the first.
    /// </summary>
    [Fact]
    public async Task Handle_PublishesOnlyTheMachinesStandingAtEachSite()
    {
        var nayax = new RecordingNayaxLynxClient(
            [
                new NayaxMachine { MachineID = FullMachineId, CustomerID = SiteId, MachineName = "Lobby" },
                new NayaxMachine { MachineID = OtherSiteMachineId, CustomerID = OtherSiteId, MachineName = "Annex" },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [FullMachineId] = [Selection(FullMachineId, par: 10, missing: 0)],
                [OtherSiteMachineId] = [Selection(OtherSiteMachineId, par: 10, missing: 10)],
            });

        var summaries = await UseCase(nayax).Handle(CancellationToken.None);

        var site = summaries.Single(summary => summary.SiteId == SiteId);
        var otherSite = summaries.Single(summary => summary.SiteId == OtherSiteId);

        Assert.Equal(new (long, decimal?)[] { (FullMachineId, 100m) },
            site.MachineStocks.Select(stock => (stock.MachineId, stock.StockPercentage)));
        Assert.Equal(new (long, decimal?)[] { (OtherSiteMachineId, 0m) },
            otherSite.MachineStocks.Select(stock => (stock.MachineId, stock.StockPercentage)));
    }

    private static GetSiteSummaries UseCase(INayaxLynxClient nayax) =>
        new(
            nayax,
            new RecordingSiteFactsStore(ActiveProducts),
            FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
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

    private static NayaxMachineProduct Selection(long machineId, int par, int missing) =>
        new()
        {
            MachineID = machineId,
            NayaxProductID = 1,
            PAR = par,
            MissingStockByMDB = missing,
            VendOutAlertThreshold = 1,
        };
}
