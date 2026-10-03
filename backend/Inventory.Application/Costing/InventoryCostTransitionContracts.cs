using Inventory.Domain.Costing;

namespace Inventory.Application.Costing;

// The inventory-cost transition's request and response contracts (issue #298, child 4 of #149),
// moved unchanged in name and shape from the former InventoryApi/DTOs/InventoryCostTransitionDtos.cs
// so the POST api/admin/inventory-cost-transition/* JSON and the stored preview snapshots keep the
// same properties. InventoryCostBaselineSource is the Domain mirror of the persisted enum, with the
// same numeric values.

public sealed record InventoryCostTransitionPreviewRequest(
    long ProductId,
    decimal AverageUnitCost,
    InventoryCostBaselineSource CostSource);

public sealed record InventoryCostTransitionMachineStockDto(
    long MachineId,
    string MachineName,
    int StockQuantity,
    string Source);

public sealed record InventoryCostTransitionPreview(
    Guid PreviewId,
    long ProductId,
    string ProductName,
    int HomeStockQuantity,
    IReadOnlyList<InventoryCostTransitionMachineStockDto> MachineStocks,
    int MachineStockQuantity,
    int OpeningCostingQuantity,
    decimal AverageUnitCost,
    decimal InventoryValue,
    DateTime CutoffAt,
    InventoryCostBaselineSource CostSource,
    int LegacyReplayedPhysicalQuantity,
    int LegacyPhysicalDiscrepancy,
    string DataQualityNote);

public sealed record ApplyInventoryCostTransitionRequest(
    Guid PreviewId,
    bool Confirmed);

public sealed record InventoryCostTransitionBatchPreviewRequest(
    InventoryCostBaselineSource CostSource);

public sealed record InventoryCostTransitionBatchPreview(
    Guid PreviewId,
    DateTime CutoffAt,
    InventoryCostBaselineSource CostSource,
    IReadOnlyList<InventoryCostTransitionPreview> Products,
    int ProductCount,
    int HomeStockQuantity,
    int MachineStockQuantity,
    int OpeningCostingQuantity,
    decimal InventoryValue);
