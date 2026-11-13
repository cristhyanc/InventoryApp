namespace Inventory.Application.Sites;

/// <summary>
/// One machine standing at a site, with that machine's own stock percentage (issue #1013) and its
/// own low and empty product counts (issue #1019).
///
/// <para><see cref="MachineId"/> is the provider's machine id - the same <c>MachineID</c> the fleet
/// read and <c>GET api/machines</c> publish - because this row identifies a machine in the provider's
/// own data rather than a local machine record, and the sites read holds no local id.</para>
///
/// <para><see cref="StockPercentage"/> is
/// <see cref="Inventory.Domain.Sites.SiteStockPolicy.CalculateMachineStockPercentage"/> over that
/// machine's selections alone, so it is the site rule applied to a narrower group and never a share
/// of the site figure. <see langword="null"/> means the machine reports <b>no capacity at all</b>,
/// which is neither 0% nor the 100% the site rule answers for a site with no capacity.</para>
///
/// <para><see cref="LowProductCount"/>/<see cref="EmptyProductCount"/> are
/// <see cref="Inventory.Domain.Sites.SiteStockPolicy.CalculateAlertCounts"/> - the site's own alert
/// rule, unchanged - over that machine's selections alone: only selections linked to an active
/// catalogue product are evaluated, several slots of one product on this machine are summed first,
/// and each is classified by <c>MachineRefillAlertPolicy.Classify</c>. They are counts rather than
/// nullable figures, so <c>0</c> and <c>0</c> is a fact about a machine whose selections were read
/// and hold nothing to alert on; a read that failed publishes no site and therefore no machine row
/// at all. A site counts a product once over its machines' combined stock, so the site's counts
/// deliberately need not equal the sum of its machines' counts.</para>
///
/// <para>Every figure here is a stock figure rather than a financial one, so issue #697's redaction
/// does not touch any of them.</para>
/// </summary>
public sealed record SiteMachineStock(
    long MachineId,
    decimal? StockPercentage,
    int LowProductCount,
    int EmptyProductCount);

/// <summary>
/// One site's dashboard row: its machine count, its stock state, and its recent completed-sale
/// revenue.
///
/// <para><b>Four sales periods and one comparison</b> (issue #1012). <see cref="TodayRevenue"/>,
/// <see cref="CurrentWeekRevenue"/>, <see cref="LastWeekRevenue"/> and
/// <see cref="MonthToDateRevenue"/> are the same four periods the machine rows publish, taken from
/// the same <see cref="Inventory.Application.Machines.MachineDashboardWindow"/>, and each is the sum
/// of the completed-sale settlement value of the machines currently standing at the site.
/// <see cref="PreviousComparableWeekRevenue"/> is unchanged and remains the baseline of the
/// comparison that belongs to the current week.</para>
///
/// <para><b>And Today's own baseline</b> (issue #1177). <see cref="PreviousComparableDayRevenue"/>
/// is <see cref="Inventory.Application.Machines.MachineDashboardWindow.PreviousComparableDay"/>
/// summed by the same rule as every figure above: yesterday's business day up to the same elapsed
/// time since business-local midnight as now is into today's, so Today has a comparison period of
/// its own exactly as This week already did. It is appended after
/// <see cref="MachineStocks"/> so every field before it keeps its name, order and value, and
/// membership is the machine's current <c>CustomerID</c> in that period too.</para>
///
/// <para><b>And Month to date's</b> (issue #1180). <see cref="PreviousComparableMonthRevenue"/> is
/// <see cref="Inventory.Application.Machines.MachineDashboardWindow.PreviousComparableMonth"/>
/// summed by that same rule: the previous calendar month up to the same point of the month - today's
/// day of it, or its last day when it is shorter - as now is into this one. It is appended after the
/// day baseline, is counted with the same policy and the same site membership as every figure above,
/// and like them it is withheld from a caller without <c>Dashboard.ViewFinancials</c>. The
/// completed-sales read reaches back to this period's own first instant, which is what makes the
/// figure complete rather than short.</para>
///
/// <para><b>And yesterday in full</b> (issue #1215). <see cref="PreviousDayRevenue"/> is
/// <see cref="Inventory.Application.Machines.MachineDashboardWindow.PreviousDay"/> summed by that
/// same rule: the whole previous business day, from its business-local midnight to today's, which
/// is the Dashboard's Today baseline. <see cref="PreviousComparableDayRevenue"/> keeps its
/// same-elapsed-time meaning. It is appended last and withheld on exactly the same terms.</para>
///
/// <para><b>Every revenue figure is withheld from a request without
/// <c>Dashboard.ViewFinancials</c></b> (issue #697). They are <see langword="null"/> for such a
/// request and <see cref="GetSiteSummaries"/> does not read the site's completed sales at all, so
/// the amount is absent because it was never calculated rather than stripped on the way out - the
/// decision issue #527 made for the Dashboard summary. <see langword="null"/> here therefore means
/// "not this caller's to see" and never "no sale was recorded": a period with no completed sale
/// reports an honest <c>0</c> to a caller who may see it.</para>
///
/// <para><b>Each machine's own stock percentage and alert counts</b> (issues #1013 and #1019).
/// <see cref="MachineStocks"/> is the per-machine split of the selections
/// <see cref="GetSiteSummaries"/> already fetches for <see cref="TotalStockPercentage"/> and the
/// counts below: one entry per machine standing at the site, in the order the fleet read returned
/// them, so the figures cost no additional provider request. <see cref="TotalStockPercentage"/>,
/// <see cref="LowProductCount"/>, <see cref="EmptyProductCount"/> and every other figure are
/// unchanged - this publishes a split that was previously discarded rather than recalculating
/// anything.</para>
/// </summary>
public sealed record SiteSummary(
    long SiteId,
    string SiteName,
    int MachineCount,
    decimal TotalStockPercentage,
    int LowProductCount,
    int EmptyProductCount,
    decimal? TodayRevenue,
    decimal? CurrentWeekRevenue,
    decimal? PreviousComparableWeekRevenue,
    decimal? LastWeekRevenue,
    decimal? MonthToDateRevenue,
    IReadOnlyList<SiteMachineStock> MachineStocks,
    decimal? PreviousComparableDayRevenue,
    decimal? PreviousComparableMonthRevenue,
    decimal? PreviousDayRevenue);
