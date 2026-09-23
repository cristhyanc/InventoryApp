namespace InventoryApi.DTOs;

public record SaleCostingBackfillResult(
    int CostedCount,
    int LegacyEstimatedCount,
    int PendingCount,
    int ErrorCount,
    int AlreadyFinalizedCount,
    bool DryRun);

public record NayaxCostBackfillResult(
    int SalesReviewed,
    int SalesWithNayaxCost,
    int SalesWouldBeCosted,
    int SalesAlreadyCosted,
    int SalesStillPending,
    int InvalidCostRows,
    int ErrorRows,
    bool DryRun);
