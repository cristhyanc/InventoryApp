using Inventory.Application.Sites;
using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

/// <summary>
/// Thin delegator to the migrated <see cref="Inventory.Application.Sites.GetSiteSummaries"/>/
/// <see cref="Inventory.Application.Sites.GetSiteProducts"/> use cases (issue #241), mapping their
/// result to the <see cref="SiteSummaryDto"/>/<see cref="SiteProductDto"/> API contracts unchanged.
/// </summary>
public class SiteService : ISiteService
{
    private readonly GetSiteSummaries _getSiteSummaries;
    private readonly GetSiteProducts _getSiteProducts;

    public SiteService(GetSiteSummaries getSiteSummaries, GetSiteProducts getSiteProducts)
    {
        _getSiteSummaries = getSiteSummaries;
        _getSiteProducts = getSiteProducts;
    }

    public async Task<List<SiteSummaryDto>> GetAll()
    {
        var summaries = await _getSiteSummaries.Handle(CancellationToken.None);
        return summaries.Select(summary => new SiteSummaryDto(
            summary.SiteId,
            summary.SiteName,
            summary.MachineCount,
            summary.TotalStockPercentage,
            summary.LowProductCount,
            summary.EmptyProductCount,
            summary.TodayRevenue,
            summary.CurrentWeekRevenue,
            summary.PreviousComparableWeekRevenue)).ToList();
    }

    public async Task<List<SiteProductDto>> GetProducts(long siteId)
    {
        var products = await _getSiteProducts.Handle(siteId, CancellationToken.None);
        return products.Select(product => new SiteProductDto(
            product.ProductId,
            product.Name,
            product.AverageUnitCost,
            product.SitePrice,
            product.EstimatedCardProfit,
            product.QuantityInStock,
            product.MaxStock)).ToList();
    }
}
