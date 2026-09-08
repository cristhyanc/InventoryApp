using InventoryApi.Models;

namespace InventoryApi.DTOs;

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
