using Inventory.Application.Access;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
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
/// operation at a time. Its today/week-to-date/previous-comparable-week periods come from the
/// business day rather than the host's local clock (issue #310; see
/// <see cref="MachineDashboardWindow"/>, shared with the machine dashboard).
///
/// <para>A mapping's <c>NayaxProductID</c> is resolved to a local product through
/// <see cref="INayaxProductLinkStore"/> (issue #733) before the stock facts are built, instead of
/// being looked up in the local catalogue's activity as if it were a local id. A mapping this
/// business holds no link for carries no product id, which is what a mapping with no
/// <c>NayaxProductID</c> already carried: it still counts toward the site's physical stock
/// percentage, exactly as an unmapped mapping always has, and is not counted as a low or empty
/// product alert.</para>
///
/// <para><b>The revenue figures depend on what the request may see (issue #697).</b> Sites is part of
/// the day-to-day inventory work, so the endpoint requires <c>Inventory.Operate</c> and every role
/// keeps reaching it; the three revenue figures additionally need
/// <c>Dashboard.ViewFinancials</c>. For a request that does not hold it
/// <see cref="ISiteFactsStore.GetRecentCompletedSalesAsync"/> is not called at all and each figure is
/// <see langword="null"/> - withheld because it was never calculated, not stripped on the way out,
/// which is the decision <c>GetDashboardSummary</c> already makes (issue #527). The decision is made
/// here rather than at the HTTP boundary because it decides what is <em>read</em>, and it is made
/// through <see cref="CurrentCapabilities"/>, the one place that answers "what may this request do" -
/// <see cref="RoleCapabilities"/> for a member, from the request's memoised membership resolution,
/// and <see cref="SupportSessionAccessPolicy"/>'s fixed set for a support session (issue #551), which
/// grants <c>Dashboard.ViewFinancials</c>. So it cannot disagree with the capability the endpoint
/// enforces or with what <c>GET /api/me/access</c> reported, and no role name appears in it. The
/// stock, alert, MDB and pricing figures are identical for every role: redaction removes the amounts
/// and changes nothing else.</para>
/// </summary>
public sealed class GetSiteSummaries
{
    private readonly INayaxLynxClient _nayax;
    private readonly ISiteFactsStore _facts;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly ISiteNameResolver _siteNames;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly CurrentCapabilities _capabilities;

    public GetSiteSummaries(
        INayaxLynxClient nayax,
        ISiteFactsStore facts,
        INayaxProductLinkStore productLinks,
        ISiteNameResolver siteNames,
        IClock clock,
        IBusinessCalendar businessCalendar,
        CurrentCapabilities capabilities)
    {
        _nayax = nayax;
        _facts = facts;
        _productLinks = productLinks;
        _siteNames = siteNames;
        _clock = clock;
        _businessCalendar = businessCalendar;
        _capabilities = capabilities;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has neither a usable membership nor a support session, so there is nothing to
    /// decide the revenue figures from. Nothing is read: an unresolved membership is a refusal, never
    /// a fall back to the least-privileged role. A request cannot reach this - the endpoint's
    /// <c>Inventory.Operate</c> policy refuses such a caller first - and it fails closed if one ever
    /// could.
    /// </exception>
    public async Task<IReadOnlyList<SiteSummary>> Handle(CancellationToken cancellationToken)
    {
        // Resolved before anything else, and awaited, because the membership this reads resolves
        // through the request's own scoped AppDbContext - the context every facts-store read below
        // lands on, and which supports one operation at a time.
        var mayViewFinancials = await _capabilities
            .AllowsAsync(Capability.DashboardViewFinancials, cancellationToken)
            .ConfigureAwait(false);

        var sites = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID.HasValue)
            .GroupBy(machine => machine.CustomerID!.Value)
            .Select(group => (SiteId: group.Key, Machines: group.ToList()))
            .ToList();

        var productActivity = (await _facts.GetProductActivityAsync(cancellationToken))
            .ToDictionary(fact => fact.ProductId, fact => fact.IsActive);
        if (sites.Count == 0) return [];

        // The per-machine Nayax requests stay a concurrent fan-out: that bound belongs to the remote
        // system, not to the database. Since issue #520 each of them does resolve the business's
        // Nayax credentials through the request's own scoped AppDbContext, so the fan-out is awaited
        // before the completed-sales read starts (issue #667) - a remote read in flight beside a
        // facts-store read would be two operations on a context that supports one.
        var pending = sites
            .Select(site => (
                site.SiteId,
                site.Machines,
                MachineProducts: Task.WhenAll(site.Machines.Select(
                    machine => _nayax.GetMachineProductsAsync(machine.MachineID, cancellationToken)))))
            .ToList();

        // Resolved while the fan-out runs, exactly where it was before: the periods every summary is
        // built from come from one instant, taken once.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);

        // One await over every site's fan-out, so a failing machine request propagates and no task
        // is left unobserved when another one fails first.
        await Task.WhenAll(pending.Select(site => site.MachineProducts));

        var sitesWithProducts = new List<(long SiteId, List<NayaxMachine> Machines, List<NayaxMachineProduct> Products)>(
            pending.Count);
        foreach (var site in pending)
        {
            sitesWithProducts.Add((site.SiteId, site.Machines, (await site.MachineProducts).SelectMany(items => items).ToList()));
        }

        // One link read for the whole fleet, awaited after the fan-out and before the completed-sales
        // read below: they land on the request's own scoped AppDbContext, which supports one
        // operation at a time.
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            sitesWithProducts
                .SelectMany(site => site.Products)
                .Where(mp => mp.NayaxProductID.HasValue)
                .Select(mp => mp.NayaxProductID!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);

        // One scoped read covers every site's machines over the same 16-day lookback the former
        // per-site reads each requested, instead of one overlapping read per site; the facts carry
        // their machine id, so each site's own sales are selected from the result in memory. The
        // lookback counts back from the current UTC instant, the time base the sale facts are stored in.
        //
        // It is not read at all for a request that may not be shown the revenue (issue #697): there
        // is no completed-sales query in that request, which is also why a withheld figure below is
        // decided by whether the sales were read - there is nothing to redact, only revenue that
        // exists and revenue that was never calculated.
        var salesByMachine = mayViewFinancials
            ? (await _facts.GetRecentCompletedSalesAsync(
                    sitesWithProducts.SelectMany(site => site.Machines).Select(machine => machine.MachineID).Distinct().ToList(),
                    window.NowUtc.AddDays(-16),
                    cancellationToken))
                .GroupBy(sale => sale.MachineId)
                .ToDictionary(group => group.Key, group => group.ToList())
            : null;

        var summaries = sitesWithProducts
            .Select(site => BuildSummary(
                site.SiteId,
                site.Machines,
                site.Products,
                SalesFor(site.Machines, salesByMachine),
                localProductIdByNayaxId,
                productActivity,
                window))
            .ToList();

        return summaries.OrderBy(summary => summary.SiteName).ToList();
    }

    /// <summary>
    /// A site's own completed sales, or <see langword="null"/> when none were read because the
    /// request may not be shown its revenue.
    /// </summary>
    private static List<SiteCompletedSaleFact>? SalesFor(
        IEnumerable<NayaxMachine> machines,
        IReadOnlyDictionary<long, List<SiteCompletedSaleFact>>? salesByMachine) =>
        salesByMachine is null
            ? null
            : machines
                .Select(machine => machine.MachineID)
                .Distinct()
                .SelectMany(machineId => salesByMachine.GetValueOrDefault(machineId, []))
                .ToList();

    private SiteSummary BuildSummary(
        long siteId,
        List<NayaxMachine> machines,
        IReadOnlyList<NayaxMachineProduct> machineProducts,
        IReadOnlyList<SiteCompletedSaleFact>? sales,
        IReadOnlyDictionary<long, long> localProductIdByNayaxId,
        IReadOnlyDictionary<long, bool> productActivity,
        MachineDashboardWindow window)
    {
        var stockFacts = machineProducts
            .Select(mp =>
            {
                var productId = mp.NayaxProductID is long nayaxProductId
                    && localProductIdByNayaxId.TryGetValue(nayaxProductId, out var localProductId)
                        ? localProductId
                        : (long?)null;

                return new SiteMachineProductStockFact(
                    productId,
                    productId.HasValue && productActivity.GetValueOrDefault(productId.Value),
                    mp.PAR ?? 0,
                    mp.MissingStockByMDB ?? 0,
                    mp.VendOutAlertThreshold ?? 0);
            })
            .ToList();
        var (lowProductCount, emptyProductCount) = SiteStockPolicy.CalculateAlertCounts(stockFacts);

        return new SiteSummary(
            siteId,
            _siteNames.Resolve(machines, siteId),
            machines.Count,
            SiteStockPolicy.CalculateStockPercentage(stockFacts),
            lowProductCount,
            emptyProductCount,
            RevenueIn(sales, window.Today),
            RevenueIn(sales, window.CurrentWeek),
            RevenueIn(sales, window.PreviousComparableWeek));
    }

    /// <summary>
    /// A period's completed-sale revenue. Both bounds are UTC instants resolved from the
    /// business day, and <c>MachineAuthorizationTime</c> is a persisted true UTC instant, so period
    /// and sale are compared in the same time base.
    ///
    /// <see langword="null"/> when <paramref name="sales"/> is <see langword="null"/>, which is the
    /// withheld case of issue #697 and not an empty period: a period a caller may see and that holds
    /// no completed sale sums to <c>0</c>.
    /// </summary>
    private static decimal? RevenueIn(
        IReadOnlyList<SiteCompletedSaleFact>? sales, MachineDashboardPeriodUtc period) =>
        sales?
            .Where(sale => sale.MachineAuthorizationTime >= period.StartUtc &&
                           sale.MachineAuthorizationTime <= period.EndUtc)
            .Sum(sale => sale.SettlementValue);
}
