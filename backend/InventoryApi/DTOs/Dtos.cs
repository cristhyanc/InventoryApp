using InventoryApi.Models;

namespace InventoryApi.DTOs;

public record ProductCreateDto(
    string Name,
    string? Sku,
    string? Description,
    decimal UnitPrice,
    int QuantityInStock,
    int LowStockThreshold,
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
    DateTime? EatBefore
);

public record CategoryDto(string Name, string? Description);

public record SupplierDto(string Name, string? ContactName, string? Phone, string? Email, string? Address);

public record ReceiptItemDto(long ProductId, decimal Quantity, decimal UnitCost);
public record ReceiptCreateMetaDto(string Title, string? Notes, decimal? TotalAmount, decimal? DeliveryCost, decimal? PackageCost, DateTime? PurchaseDate, int? SupplierId, IReadOnlyList<ReceiptItemDto>? Items = null);

public record ReceiptValidationDto(
    bool HasTotalMismatch,
    decimal? CalculatedItemSubtotal,
    decimal? CalculatedTotal,
    decimal? TotalDifference
);

public record ReceiptResponseDto(
    Receipt Receipt,
    ReceiptValidationDto? Validation = null
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
