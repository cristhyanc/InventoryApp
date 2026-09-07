using InventoryApi.DTOs;

namespace InventoryApi.Services.Interfaces;

public interface ISiteCommissionService
{
    Task<SiteCommissionReportDto> GetReportAsync(DateTime from, DateTime to, long? siteId, CancellationToken cancellationToken = default);
}
