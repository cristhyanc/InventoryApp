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
    string? Notes
);

public record CategoryDto(string Name, string? Description);

public record SupplierDto(string Name, string? ContactName, string? Phone, string? Email, string? Address);

public record ReceiptCreateMetaDto(string Title, string? Notes, decimal? TotalAmount, DateTime? PurchaseDate, int? SupplierId);
