using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public enum InventoryCostBaselineSource
{
    ManualAuthoritative = 1,
    ManualEstimated = 2
}

public class InventoryCostTransitionBaseline : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public long ProductId { get; set; }
    public virtual Product? Product { get; set; }
    public DateTime CutoffAt { get; set; }
    public int HomeStockQuantity { get; set; }
    public int MachineStockQuantity { get; set; }
    public int OpeningCostingQuantity { get; set; }
    public decimal AverageUnitCost { get; set; }
    public decimal InventoryValue { get; set; }
    public InventoryCostBaselineSource CostSource { get; set; }
    public int LegacyReplayedPhysicalQuantity { get; set; }
    public int LegacyPhysicalDiscrepancy { get; set; }
    public string DataQualityNote { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public virtual ICollection<InventoryCostTransitionMachineStock> MachineStocks { get; set; } =
        new List<InventoryCostTransitionMachineStock>();
}

public class InventoryCostTransitionMachineStock : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int InventoryCostTransitionBaselineId { get; set; }
    public virtual InventoryCostTransitionBaseline? InventoryCostTransitionBaseline { get; set; }
    public long MachineId { get; set; }
    public string? MachineName { get; set; }
    public int StockQuantity { get; set; }
    public string Source { get; set; } = "Nayax PAR - MissingStockByMDB";
}

public class InventoryCostTransitionPreviewDraft : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public Guid Id { get; set; }
    public long ProductId { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}
