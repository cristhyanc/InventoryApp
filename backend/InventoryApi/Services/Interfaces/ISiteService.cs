using InventoryApi.DTOs;

namespace InventoryApi.Services.Interfaces;

public interface ISiteService
{
    Task<List<SiteSummaryDto>> GetAll();
    Task<List<SiteProductDto>> GetProducts(long siteId);
}
