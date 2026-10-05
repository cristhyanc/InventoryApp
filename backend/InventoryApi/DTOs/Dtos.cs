// Imported for StockAdjustmentDto.Reason only - the temporary compatibility exception documented on
// that record (issue #305). No other DTO in this file names the persistence model.
using Inventory.Infrastructure.Models;

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

/// <summary>
/// The stock adjustment a client posts to "/api/products/{productId}/stock".
///
/// <see cref="Reason"/> is the persistence enum rather than an API-owned copy, which is why this
/// file still imports <c>Inventory.Infrastructure.Models</c>. That is a <b>temporary compatibility exception</b>
/// (issue #305), not the target state: the published document carries one
/// <c>StockAdjustmentReason</c> component derived from that CLR enum, reached from this request
/// body, from the pinned <c>StockAdjustment</c> response component and from the legacy
/// <c>Product</c> component that <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c>
/// regenerates, so an API-owned enum of the same simple name makes Swashbuckle fail document
/// generation with a duplicate-schema-id error, and renaming or duplicating the published component
/// is an API-contract change issue #305 excludes. The enum moves with the persistence models
/// (#153/#154), together with the response side's
/// <c>ProductStockAdjustmentResponse.Reason</c>/<c>Source</c>.
///
/// Until then, <c>InventoryApi.Tests.DTOs.StockAdjustmentRequestJsonContractTests</c> pins the
/// numeric values this body binds from and the member-for-member agreement with the Domain enum the
/// controller casts to, and <c>InventoryApi.Tests.Swagger.StockAndExpenseSchemaContractTests</c>
/// pins the published request schema and reproduces the collision that forces the exception.
/// </summary>
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
