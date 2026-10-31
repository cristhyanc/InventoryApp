using Inventory.Application.Access;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Sites;

namespace Inventory.Application.Sites;

/// <summary>
/// The site dashboard listing use case: groups the live Nayax machine fleet by site (<c>CustomerID</c>)
/// and, per site, aggregates its machines' stock and recent completed-sale revenue. Mirrors the former
/// <c>InventoryApi.Services.SiteService.GetAll</c>/<c>BuildSummary</c> exactly (issue #241), including
/// the per-machine fan-out to Nayax through <see cref="INayaxLynxClient.GetMachineProductsAsync"/>,
/// which runs at most <see cref="CalculateReorderNeeds.MaxConcurrentMachineRequests"/> requests at a
/// time across the whole fleet (issue #845) rather than one per machine at once.
/// The two <see cref="ISiteFactsStore"/> reads are awaited one at a time and the completed-sale facts
/// for every site are loaded by one scoped read and distributed per machine in memory (issue #313),
/// because the scoped store's EF adapter shares a single <c>AppDbContext</c>, which supports only one
/// operation at a time. Its periods come from the business day rather than the host's local clock
/// (issue #310; see <see cref="MachineDashboardWindow"/>, shared with the machine dashboard).
///
/// <para><b>Four published sales periods, and no period arithmetic of its own</b> (issue #1012). A
/// summary publishes today, week-to-date, last week and month-to-date revenue, plus the
/// previous-comparable-week baseline of the one comparison these figures carry. Each is
/// <see cref="MachineDashboardWindow"/>'s own period of that name, compared against the same
/// persisted instants with the same inclusive bounds the machine rows use, which is what makes a
/// site's figure the sum of the gross revenue of the machine rows grouped under it for every one of
/// the four. Membership is the machine's current <c>CustomerID</c> in every period, as it already
/// was for today and week-to-date: a sale is counted at the site its machine stands at now, never at
/// the site it stood at when the sale happened. The completed-sales read's lower bound is derived
/// from the window too (see <see cref="EarliestPublishedPeriodStartUtc"/>) rather than from a fixed
/// day count, so every published period is read in full from its own first instant.</para>
///
/// <para><b>Each machine's own stock percentage</b> (issue #1013). The per-machine selections this
/// use case already fetches for the site percentage were previously flattened and the split
/// discarded; they are now also published per machine, as
/// <see cref="SiteSummary.MachineStocks"/>, through
/// <see cref="SiteStockPolicy.CalculateMachineStockPercentage"/> - the site rule over one machine's
/// selections, with <see langword="null"/> for a machine that reports no capacity at all. No
/// additional Nayax request is made for them: they come from the same one cached
/// <see cref="INayaxLynxClient.GetMachineProductsAsync"/> call per machine the fan-out above already
/// makes, so the request volume and the 60-second cache freshness are the site figure's own. Each
/// machine's figure is attributed to the machine its request was made <em>for</em>, not to the
/// <c>MachineID</c> a selection echoes back, and the site percentage and alert counts are built from
/// the same facts concatenated in machine order, so they are unchanged for the same provider
/// data.</para>
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
/// keeps reaching it; the revenue figures additionally need
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

        // The periods every summary is built from come from one instant, taken once, before the
        // fan-out below rather than beside it.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);

        // The per-machine Nayax requests stay a concurrent fan-out - that bound belongs to the remote
        // system, not to the database - but a bounded one (issue #845): at most
        // CalculateReorderNeeds.MaxConcurrentMachineRequests of them are in flight across the whole
        // fleet, the same limit CalculateReorderNeeds and GetPickList already fan this exact call out
        // under, rather than one request per machine at once. Since issue #520 each of them does
        // resolve the business's Nayax credentials through the request's own scoped AppDbContext, so
        // the fan-out completes before the completed-sales read starts (issue #667) - a remote read in
        // flight beside a facts-store read would be two operations on a context that supports one -
        // and Parallel.ForEachAsync observes every request, so a failing one propagates and none is
        // left unobserved when another fails first.
        //
        // Each request writes to the slot of the machine it was made for, so a site's mappings are the
        // same sequence in the same order whatever order the bounded requests completed in.
        var machineRequests = sites
            .SelectMany((site, siteIndex) => site.Machines.Select((machine, machineIndex) =>
                (SiteIndex: siteIndex, MachineIndex: machineIndex, machine.MachineID)))
            .ToList();
        var productsByMachine = sites
            .Select(site => new List<NayaxMachineProduct>[site.Machines.Count])
            .ToList();

        await Parallel.ForEachAsync(
            machineRequests,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = CalculateReorderNeeds.MaxConcurrentMachineRequests,
                CancellationToken = cancellationToken,
            },
            async (request, machineCancellationToken) =>
            {
                productsByMachine[request.SiteIndex][request.MachineIndex] =
                    await _nayax.GetMachineProductsAsync(request.MachineID, machineCancellationToken);
            });

        // The per-machine lists are kept rather than flattened here (issue #1013): each machine's own
        // stock percentage is that machine's selections alone, and the slot a request wrote to is the
        // machine it was made *for*, so an echoed-back MachineID cannot attribute a selection to
        // another machine's row. The site figures are built from the same lists concatenated in that
        // order below, which is exactly the sequence this step used to hand over.
        var sitesWithProducts = sites
            .Select((site, siteIndex) => (
                site.SiteId,
                site.Machines,
                ProductsByMachine: productsByMachine[siteIndex]))
            .ToList();

        // One link read for the whole fleet, awaited after the fan-out and before the completed-sales
        // read below: they land on the request's own scoped AppDbContext, which supports one
        // operation at a time.
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            sitesWithProducts
                .SelectMany(site => site.ProductsByMachine)
                .SelectMany(products => products)
                .Where(mp => mp.NayaxProductID.HasValue)
                .Select(mp => mp.NayaxProductID!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);

        // One scoped read covers every site's machines, instead of one overlapping read per site;
        // the facts carry their machine id, so each site's own sales are selected from the result in
        // memory. Its lower bound is derived from the window - the first instant of the earliest
        // period a summary publishes - rather than from a day count (issue #1012), so the read
        // always covers every published period in full. A fixed count cannot: a 16-day lookback
        // covered last week but reached only part of month to date, which after about the sixteenth
        // of a month silently shortened that figure. The bound is in UTC, the time base the sale
        // facts are stored in, and inclusive, so a sale at exactly a period's start is counted by
        // the read and by RevenueIn alike.
        //
        // It is not read at all for a request that may not be shown the revenue (issue #697): there
        // is no completed-sales query in that request, which is also why a withheld figure below is
        // decided by whether the sales were read - there is nothing to redact, only revenue that
        // exists and revenue that was never calculated.
        var salesByMachine = mayViewFinancials
            ? (await _facts.GetRecentCompletedSalesAsync(
                    sitesWithProducts.SelectMany(site => site.Machines).Select(machine => machine.MachineID).Distinct().ToList(),
                    EarliestPublishedPeriodStartUtc(window),
                    cancellationToken))
                .GroupBy(sale => sale.MachineId)
                .ToDictionary(group => group.Key, group => group.ToList())
            : null;

        var summaries = sitesWithProducts
            .Select(site => BuildSummary(
                site.SiteId,
                site.Machines,
                site.ProductsByMachine,
                SalesFor(site.Machines, salesByMachine),
                localProductIdByNayaxId,
                productActivity,
                window))
            .ToList();

        return summaries.OrderBy(summary => summary.SiteName).ToList();
    }

    /// <summary>
    /// The first instant any period a summary publishes begins at, which is how far back the
    /// completed-sales read has to reach (issue #1012). It is a minimum over the published periods
    /// rather than a period of its own, because which of them starts earliest depends on the date:
    /// month to date is the earliest for most of a month, and last week - which the previous
    /// comparable week shares a start with - is earlier in the first half of one.
    /// <see cref="MachineDashboardWindow.TwoWeeksAgo"/> is deliberately absent: a site row publishes
    /// no figure for it, so reading it would be reading sales nothing reports.
    /// </summary>
    private static DateTime EarliestPublishedPeriodStartUtc(MachineDashboardWindow window) =>
        new[]
        {
            window.Today.StartUtc,
            window.CurrentWeek.StartUtc,
            window.PreviousComparableWeek.StartUtc,
            window.LastWeek.StartUtc,
            window.MonthToDate.StartUtc,
        }.Min();

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

    /// <summary>
    /// One site's row. <paramref name="productsByMachine"/> holds the selections of each machine in
    /// <paramref name="machines"/>, in the same order and by the same index, which is what lets each
    /// machine's own percentage be attributed to the machine its request was made for (issue #1013).
    /// </summary>
    private SiteSummary BuildSummary(
        long siteId,
        List<NayaxMachine> machines,
        IReadOnlyList<List<NayaxMachineProduct>> productsByMachine,
        IReadOnlyList<SiteCompletedSaleFact>? sales,
        IReadOnlyDictionary<long, long> localProductIdByNayaxId,
        IReadOnlyDictionary<long, bool> productActivity,
        MachineDashboardWindow window)
    {
        var stockFactsByMachine = productsByMachine
            .Select(products => products
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
                .ToList())
            .ToList();

        // The site's facts are its machines' facts concatenated in machine order, which is the
        // sequence the site figures were always built from: the split below is published beside them
        // rather than instead of them, so no site figure changes for the same provider data.
        var stockFacts = stockFactsByMachine.SelectMany(facts => facts).ToList();
        var (lowProductCount, emptyProductCount) = SiteStockPolicy.CalculateAlertCounts(stockFacts);

        var machineStocks = machines
            .Select((machine, index) => new SiteMachineStock(
                machine.MachineID,
                SiteStockPolicy.CalculateMachineStockPercentage(stockFactsByMachine[index])))
            .ToList();

        return new SiteSummary(
            siteId,
            _siteNames.Resolve(machines, siteId),
            machines.Count,
            SiteStockPolicy.CalculateStockPercentage(stockFacts),
            lowProductCount,
            emptyProductCount,
            RevenueIn(sales, window.Today),
            RevenueIn(sales, window.CurrentWeek),
            RevenueIn(sales, window.PreviousComparableWeek),
            RevenueIn(sales, window.LastWeek),
            RevenueIn(sales, window.MonthToDate),
            machineStocks);
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
