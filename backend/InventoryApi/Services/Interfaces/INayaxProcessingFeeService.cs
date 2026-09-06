using InventoryApi.DTOs;

namespace InventoryApi.Services.Interfaces;

public interface INayaxProcessingFeeService
{
    Task<NayaxProcessingFeeResult> GetProcessingFeesAsync(
        DateTime fromDate,
        DateTime toDate,
        long? machineId = null,
        CancellationToken cancellationToken = default);
}
