using InventoryApi.Models;
using InventoryApi.DTOs;

namespace InventoryApi.Services.Interfaces;

public interface ISaleCostingService
{
    Task CostSaleAsync(NayaxSales sale, bool allowLegacyEstimate = false, CancellationToken cancellationToken = default);
    Task<int> CostPendingSalesAsync(long? productId = null, bool allowLegacyEstimate = false, CancellationToken cancellationToken = default);
    Task<decimal?> GetAverageUnitCostAtAsync(long productId, DateTime saleTime, CancellationToken cancellationToken = default);
    Task<SaleCostingBackfillResult> BackfillAsync(bool dryRun = true, bool force = false, CancellationToken cancellationToken = default);
}
