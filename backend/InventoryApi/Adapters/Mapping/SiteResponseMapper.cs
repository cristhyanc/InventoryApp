using Inventory.Application.Sites;
using InventoryApi.DTOs;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="SiteSummary"/>/<see cref="SiteProductRecord"/> onto
/// the <see cref="SiteSummaryDto"/>/<see cref="SiteProductDto"/> contracts the site endpoints have
/// always published (issue #302). It replaces the identical step the retired <c>SiteService</c>
/// delegator took, and copies already-resolved values only: the stock percentages, alert counts,
/// revenues, average site price and estimated card profit are decided by
/// <see cref="GetSiteSummaries"/>/<see cref="GetSiteProducts"/> and their Domain policies, so this
/// mapping cannot introduce a second copy of a site rule. An unavailable estimated profit stays
/// <c>null</c> on the way through.
/// </summary>
public static class SiteResponseMapper
{
    public static SiteSummaryDto ToDto(SiteSummary summary) => new(
        summary.SiteId,
        summary.SiteName,
        summary.MachineCount,
        summary.TotalStockPercentage,
        summary.LowProductCount,
        summary.EmptyProductCount,
        summary.TodayRevenue,
        summary.CurrentWeekRevenue,
        summary.PreviousComparableWeekRevenue);

    public static SiteProductDto ToDto(SiteProductRecord product) => new(
        product.ProductId,
        product.Name,
        product.AverageUnitCost,
        product.SitePrice,
        product.EstimatedCardProfit,
        product.QuantityInStock,
        product.MaxStock);
}
