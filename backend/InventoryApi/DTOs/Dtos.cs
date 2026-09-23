using InventoryApi.Models;

namespace InventoryApi.DTOs;

public record ProductCreateDto(
    string Name,
    string? Sku,
    string? Description,
    decimal UnitPrice,
    int QuantityInStock,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    int? CategoryId,
    int? SupplierId,
    bool IsActive,
    decimal? InitialUnitCost = null
);

public record ProductUpdateDto(
    string Name,
    string? Sku,
    string? Description,
    decimal UnitPrice,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    int? CategoryId,
    int? SupplierId,
    bool IsActive
);

public record StockAdjustmentDto(
    int QuantityChange,
    StockAdjustmentReason Reason,
    string? Notes,
    long? MachineId,
    DateTime? EatBefore,
    decimal? UnitCost = null
);

public record RestockCostSuggestionDto(
    decimal? UnitCost,
    string Source,
    DateTime? PurchaseDate
);

public record CategoryDto(string Name, string? Description);

public record SupplierDto(string Name, string? ContactName, string? Phone, string? Email, string? Address);

public record PurchaseItemDto(long ProductId, decimal Quantity, decimal UnitCost);
public record PurchaseCreateMetaDto(string Title, string? Notes, decimal? TotalAmount, decimal? DeliveryCost, decimal? PackageCost, DateTime? PurchaseDate, int? SupplierId, IReadOnlyList<PurchaseItemDto>? Items = null);

public record PurchaseValidationDto(
    bool HasTotalMismatch,
    decimal? CalculatedItemSubtotal,
    decimal? CalculatedTotal,
    decimal? TotalDifference
);

// Property names stay "Receipt"/"Validation" (JSON keys "receipt"/"validation") to preserve
// the existing API contract; only the referenced CLR type is the renamed Purchase business type.
public record PurchaseResponseDto(
    Purchase Receipt,
    PurchaseValidationDto? Validation = null
);

public record SiteSummaryDto(
    long SiteId,
    string SiteName,
    int MachineCount,
    decimal TotalStockPercentage,
    int LowProductCount,
    int EmptyProductCount,
    decimal TodayRevenue,
    decimal CurrentWeekRevenue,
    decimal PreviousComparableWeekRevenue
);

public record SiteProductDto(
    long ProductId,
    string Name,
    decimal? AverageUnitCost,
    decimal SitePrice,
    decimal? EstimatedCardProfit,
    int QuantityInStock,
    int MaxStock
);
