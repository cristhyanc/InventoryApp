namespace Inventory.Application.Sites;

/// <summary>
/// One machine standing at a site, with that machine's own stock percentage (issue #1013).
///
/// <para><see cref="MachineId"/> is the provider's machine id - the same <c>MachineID</c> the fleet
/// read and <c>GET api/machines</c> publish - because this row identifies a machine in the provider's
/// own data rather than a local machine record, and the sites read holds no local id.</para>
///
/// <para><see cref="StockPercentage"/> is
/// <see cref="Inventory.Domain.Sites.SiteStockPolicy.CalculateMachineStockPercentage"/> over that
/// machine's selections alone, so it is the site rule applied to a narrower group and never a share
/// of the site figure. <see langword="null"/> means the machine reports <b>no capacity at all</b>,
/// which is neither 0% nor the 100% the site rule answers for a site with no capacity. It is a stock
/// figure rather than a financial one, so issue #697's redaction does not touch it.</para>
/// </summary>
public sealed record SiteMachineStock(long MachineId, decimal? StockPercentage);

/// <summary>
/// One site's dashboard row: its machine count, its stock state, and its recent completed-sale
/// revenue.
///
/// <para><b>Four sales periods and one comparison</b> (issue #1012). <see cref="TodayRevenue"/>,
/// <see cref="CurrentWeekRevenue"/>, <see cref="LastWeekRevenue"/> and
/// <see cref="MonthToDateRevenue"/> are the same four periods the machine rows publish, taken from
/// the same <see cref="Inventory.Application.Machines.MachineDashboardWindow"/>, and each is the sum
/// of the completed-sale settlement value of the machines currently standing at the site.
/// <see cref="PreviousComparableWeekRevenue"/> is unchanged and remains the baseline of the one
/// comparison these figures carry, which belongs to the current week.</para>
///
/// <para><b>Every revenue figure is withheld from a request without
/// <c>Dashboard.ViewFinancials</c></b> (issue #697). They are <see langword="null"/> for such a
/// request and <see cref="GetSiteSummaries"/> does not read the site's completed sales at all, so
/// the amount is absent because it was never calculated rather than stripped on the way out - the
/// decision issue #527 made for the Dashboard summary. <see langword="null"/> here therefore means
/// "not this caller's to see" and never "no sale was recorded": a period with no completed sale
/// reports an honest <c>0</c> to a caller who may see it.</para>
///
/// <para><b>Each machine's own stock percentage</b> (issue #1013). <see cref="MachineStocks"/> is the
/// per-machine split of the selections <see cref="GetSiteSummaries"/> already fetches for
/// <see cref="TotalStockPercentage"/>: one entry per machine standing at the site, in the order the
/// fleet read returned them, so the figures cost no additional provider request.
/// <see cref="TotalStockPercentage"/>, the alert counts and every other figure are unchanged - this
/// publishes a split that was previously discarded rather than recalculating anything.</para>
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
    IReadOnlyList<SiteMachineStock> MachineStocks);
