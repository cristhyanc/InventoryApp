using Inventory.Application.Nayax;
using Inventory.Domain.Machines;
using Inventory.Domain.Sites;

namespace Inventory.Application.Sites;

/// <summary>
/// The site dashboard listing use case: groups the live Nayax machine fleet by site (<c>CustomerID</c>)
/// and, per site, aggregates its machines' stock and recent completed-sale revenue. Mirrors the former
/// <c>InventoryApi.Services.SiteService.GetAll</c>/<c>BuildSummary</c> exactly (issue #241), including
/// the bounded per-site fan-out to Nayax through <see cref="INayaxLynxClient.GetMachineProductsAsync"/>.
/// The two <see cref="ISiteFactsStore"/> reads are awaited one at a time and the completed-sale facts
/// for every site are loaded by one scoped read and distributed per machine in memory (issue #313),
/// because the scoped store's EF adapter shares a single <c>AppDbContext</c>, which supports only one
/// operation at a time.
/// </summary>
public sealed class GetSiteSummaries
{
    private readonly INayaxLynxClient _nayax;
    private readonly ISiteFactsStore _facts;
    private readonly ISiteNameResolver _siteNames;

    public GetSiteSummaries(INayaxLynxClient nayax, ISiteFactsStore facts, ISiteNameResolver siteNames)
    {
        _nayax = nayax;
        _facts = facts;
        _siteNames = siteNames;
    }

    public async Task<IReadOnlyList<SiteSummary>> Handle(CancellationToken cancellationToken)
    {
        var sites = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID.HasValue)
            .GroupBy(machine => machine.CustomerID!.Value)
            .Select(group => (SiteId: group.Key, Machines: group.ToList()))
            .ToList();

        var productActivity = (await _facts.GetProductActivityAsync(cancellationToken))
            .ToDictionary(fact => fact.ProductId, fact => fact.IsActive);
        if (sites.Count == 0) return [];

        // The per-machine Nayax requests stay a concurrent fan-out, unchanged: they are independent
        // remote reads that touch no AppDbContext. Only the facts-store reads are serialized.
        var pending = sites
            .Select(site => (
                site.SiteId,
                site.Machines,
                MachineProducts: Task.WhenAll(site.Machines.Select(
                    machine => _nayax.GetMachineProductsAsync(machine.MachineID, cancellationToken)))))
            .ToList();

        var machineProductsTask = Task.WhenAll(pending.Select(site => site.MachineProducts));

        // One scoped read covers every site's machines over the same 16-day lookback the former
        // per-site reads each requested, instead of one overlapping read per site; the facts carry
        // their machine id, so each site's own sales are selected from the result in memory.
        var now = DateTime.Now;
        var salesTask = _facts.GetRecentCompletedSalesAsync(
            pending.SelectMany(site => site.Machines).Select(machine => machine.MachineID).Distinct().ToList(),
            now.AddDays(-16),
            cancellationToken);

        // Awaits both together, as before, so a failure on either side propagates and no fan-out
        // task is left unobserved when the other side fails first.
        await Task.WhenAll(machineProductsTask, salesTask);
        var salesByMachine = (await salesTask)
            .GroupBy(sale => sale.MachineId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var summaries = new List<SiteSummary>(pending.Count);
        foreach (var site in pending)
        {
            var machineProducts = (await site.MachineProducts).SelectMany(items => items).ToList();
            summaries.Add(BuildSummary(
                site.SiteId, site.Machines, machineProducts, SalesFor(site.Machines, salesByMachine), productActivity, now));
        }

        return summaries.OrderBy(summary => summary.SiteName).ToList();
    }

    private static List<SiteCompletedSaleFact> SalesFor(
        IEnumerable<NayaxMachine> machines,
        IReadOnlyDictionary<long, List<SiteCompletedSaleFact>> salesByMachine) =>
        machines
            .Select(machine => machine.MachineID)
            .Distinct()
            .SelectMany(machineId => salesByMachine.GetValueOrDefault(machineId, []))
            .ToList();

    private SiteSummary BuildSummary(
        long siteId,
        List<NayaxMachine> machines,
        IReadOnlyList<NayaxMachineProduct> machineProducts,
        IReadOnlyList<SiteCompletedSaleFact> sales,
        IReadOnlyDictionary<long, bool> productActivity,
        DateTime now)
    {
        var today = now.Date;
        var currentWeek = MachineDashboardPeriods.WeekToDate(now);
        var previousComparableWeek = MachineDashboardPeriods.PreviousComparableWeek(now);

        var stockFacts = machineProducts
            .Select(mp => new SiteMachineProductStockFact(
                mp.NayaxProductID,
                mp.NayaxProductID.HasValue && productActivity.GetValueOrDefault(mp.NayaxProductID.Value),
                mp.PAR ?? 0,
                mp.MissingStockByMDB ?? 0,
                mp.VendOutAlertThreshold ?? 0))
            .ToList();
        var (lowProductCount, emptyProductCount) = SiteStockPolicy.CalculateAlertCounts(stockFacts);

        return new SiteSummary(
            siteId,
            _siteNames.Resolve(machines, siteId),
            machines.Count,
            SiteStockPolicy.CalculateStockPercentage(stockFacts),
            lowProductCount,
            emptyProductCount,
            sales.Where(sale => sale.MachineAuthorizationTime >= today && sale.MachineAuthorizationTime <= now)
                .Sum(sale => sale.SettlementValue),
            sales.Where(sale => sale.MachineAuthorizationTime >= currentWeek.Start && sale.MachineAuthorizationTime <= currentWeek.End)
                .Sum(sale => sale.SettlementValue),
            sales.Where(sale => sale.MachineAuthorizationTime >= previousComparableWeek.Start && sale.MachineAuthorizationTime <= previousComparableWeek.End)
                .Sum(sale => sale.SettlementValue));
    }
}
