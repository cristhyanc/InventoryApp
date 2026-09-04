using InventoryApi.Models;

namespace InventoryApi.DTOs;

public record SaleCostingBackfillResult(
    int CostedCount,
    int LegacyEstimatedCount,
    int PendingCount,
    int ErrorCount,
    int AlreadyFinalizedCount,
    bool DryRun);
