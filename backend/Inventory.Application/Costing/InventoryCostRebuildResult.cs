namespace Inventory.Application.Costing;

public sealed record InventoryCostDataQualityIssue(string Code, string Message);

/// <summary>
/// The outcome of <see cref="IRebuildProductCost.RebuildAsync"/>, moved unchanged from the former
/// <c>InventoryApi.Services.InventoryCostRebuildResult</c> (issue #296).
/// </summary>
public sealed class InventoryCostRebuildResult
{
    public long ProductId { get; init; }
    public int PhysicalQuantity { get; init; }
    public int CostingQuantity { get; init; }
    public decimal InventoryValue { get; init; }
    public decimal? AverageUnitCost { get; init; }
    public int RecostedSaleCount { get; init; }
    public bool DryRun { get; init; }
    public IReadOnlyList<InventoryCostDataQualityIssue> Issues { get; init; } = Array.Empty<InventoryCostDataQualityIssue>();
    public bool HasDataQualityIssues => Issues.Count > 0;
}
