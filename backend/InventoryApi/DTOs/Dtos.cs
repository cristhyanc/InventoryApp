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
    bool IsActive
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

public record ReceiptCreateMetaDto(string Title, string? Notes, decimal? TotalAmount, decimal? DeliveryCost, decimal? PackageCost, DateTime? PurchaseDate, int? SupplierId);

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
    decimal UnitPrice,
    decimal SitePrice,
    decimal Profit,
    int QuantityInStock,
    int MaxStock
);
