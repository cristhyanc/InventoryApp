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

public record CategoryResponse(long Id, string Name, string? Description);

public record SupplierDto(string Name, string? ContactName, string? Phone, string? Email, string? Address);

public record SupplierResponse(int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address);

public record PurchaseItemDto(long ProductId, decimal Quantity, decimal UnitCost);
public record PurchaseCreateMetaDto(string Title, string? Notes, decimal? TotalAmount, decimal? DeliveryCost, decimal? PackageCost, DateTime? PurchaseDate, int? SupplierId, IReadOnlyList<PurchaseItemDto>? Items = null);

public record PurchaseValidationDto(
    bool HasTotalMismatch,
    decimal? CalculatedItemSubtotal,
    decimal? CalculatedTotal,
    decimal? TotalDifference
);

// The envelope the purchase endpoints return: the business record under the canonical "purchase"
// key (issue #127) next to its total-validation block. Issue #304 replaced the EF Purchase entity
// in that key with the API-owned PurchaseResponse; the serialized envelope is unchanged.
public record PurchaseResponseDto(
    PurchaseResponse Purchase,
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
