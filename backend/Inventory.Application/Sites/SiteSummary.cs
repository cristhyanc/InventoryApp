namespace Inventory.Application.Sites;

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
    decimal? MonthToDateRevenue);
