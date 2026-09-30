using Inventory.Application.Nayax;
using Inventory.Domain.Machines;
using Inventory.Domain.Sites;

namespace Inventory.Application.Sites;

/// <summary>
/// The site dashboard listing use case: groups the live Nayax machine fleet by site (<c>CustomerID</c>)
/// and, per site, aggregates its machines' stock and recent completed-sale revenue. Mirrors the former
/// <c>InventoryApi.Services.SiteService.GetAll</c>/<c>BuildSummary</c> exactly (issue #241), including
/// the bounded per-site fan-out to Nayax through <see cref="INayaxLynxClient.GetMachineProductsAsync"/>.
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
            .ToList();

        var productActivity = (await _facts.GetProductActivityAsync(cancellationToken))
            .ToDictionary(fact => fact.ProductId, fact => fact.IsActive);

        var summaries = await Task.WhenAll(
            sites.Select(site => BuildSummary(site.Key, site.ToList(), productActivity, cancellationToken)));

        return summaries.OrderBy(summary => summary.SiteName).ToList();
    }

    private async Task<SiteSummary> BuildSummary(
        long siteId,
        List<NayaxMachine> machines,
        IReadOnlyDictionary<long, bool> productActivity,
        CancellationToken cancellationToken)
    {
        var machineProductsTask = Task.WhenAll(
            machines.Select(machine => _nayax.GetMachineProductsAsync(machine.MachineID, cancellationToken)));
        var now = DateTime.Now;
        var salesTask = _facts.GetRecentCompletedSalesAsync(
            machines.Select(machine => machine.MachineID).ToList(), now.AddDays(-16), cancellationToken);

        await Task.WhenAll(machineProductsTask, salesTask);

        var machineProducts = (await machineProductsTask).SelectMany(items => items).ToList();
        var sales = await salesTask;
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
