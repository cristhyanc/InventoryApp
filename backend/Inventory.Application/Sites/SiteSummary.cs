namespace Inventory.Application.Sites;

/// <summary>
/// One site's dashboard row: its machine count, its stock state, and its recent completed-sale
/// revenue.
///
/// <para><b>The three revenue figures are withheld from a request without
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
    decimal? PreviousComparableWeekRevenue);
