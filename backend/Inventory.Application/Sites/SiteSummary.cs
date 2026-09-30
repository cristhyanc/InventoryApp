namespace Inventory.Application.Sites;

public sealed record SiteSummary(
    long SiteId,
    string SiteName,
    int MachineCount,
    decimal TotalStockPercentage,
    int LowProductCount,
    int EmptyProductCount,
    decimal TodayRevenue,
    decimal CurrentWeekRevenue,
    decimal PreviousComparableWeekRevenue);
