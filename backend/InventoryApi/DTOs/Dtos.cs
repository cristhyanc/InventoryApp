// Imported for StockAdjustmentDto.Reason only - the temporary compatibility exception documented on
// that record (issue #305). No other DTO in this file names the persistence model.
using Inventory.Infrastructure.Models;
// The GST classification a purchase line carries is a Domain vocabulary type, not a persistence one.
using Inventory.Domain.Gst;

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

/// <summary>
/// One purchase line in the JSON <c>items</c> field of a purchase create/update form (issue #429).
/// <c>gstClassification</c> is optional: omitting it leaves a new line unclassified and an edited
/// line's stored classification untouched. The classification's provenance is never submitted -
/// the server records a person's explicit choice as <c>Manual</c>. A value outside the published
/// <c>GstClassification</c> enum is rejected with <c>400</c>, because the JSON number would
/// otherwise bind to an undefined state no GST rule describes.
///
/// <c>id</c> is the stored line's own id, as a purchase read returns it. It is optional: a create
/// ignores it, and an update that omits it matches lines by product exactly as before. Sending it
/// is what lets an update of a purchase that holds several lines for one product keep each line's
/// classification on the right line; when those lines disagree about their classification and no
/// id is sent, the update is refused rather than guessing.
/// </summary>
public record PurchaseItemDto(
    long ProductId,
    decimal Quantity,
    decimal UnitCost,
    GstClassification? GstClassification = null,
    int? Id = null);
public record PurchaseCreateMetaDto(string Title, string? Notes, decimal? TotalAmount, decimal? DeliveryCost, decimal? PackageCost, DateTime? PurchaseDate, int? SupplierId, IReadOnlyList<PurchaseItemDto>? Items = null);

public record PurchaseValidationDto(
    bool HasTotalMismatch,
    decimal? CalculatedItemSubtotal,
    decimal? CalculatedTotal,
    decimal? TotalDifference
);

/// <summary>
/// The saved purchase's input GST (issue #431), calculated by
/// <c>Inventory.Application.Purchases.ComputePurchaseGstSummary</c> over the one authoritative
/// <c>Inventory.Domain.Purchases.PurchaseGstPolicy</c>. A client displays these figures and never
/// derives GST from an amount itself.
///
/// <see cref="InputGst"/> is the sum of the individually rounded GST amounts of the taxable
/// components only. Unclassified components contribute nothing to it and are reported separately as
/// <see cref="UnresolvedComponentCount"/> and <see cref="UnresolvedAmount"/>, so a purchase stays
/// visibly incomplete instead of looking like a resolved <c>$0</c>. A delivery or package charge
/// that is null or zero is not a component at all and is never unresolved.
///
/// It describes what is stored. It never reflects edits a client has not saved.
/// </summary>
public record PurchaseGstSummaryDto(
    decimal InputGst,
    int UnresolvedComponentCount,
    decimal UnresolvedAmount
);

// The envelope the purchase endpoints return: the business record under the canonical "purchase"
// key (issue #127) next to its total-validation block. Issue #304 replaced the EF Purchase entity
// in that key with the API-owned PurchaseResponse; the serialized envelope is unchanged. Issue #431
// added the "gst" member after them, additively: "purchase" and "validation" keep their names,
// order and values.
public record PurchaseResponseDto(
    PurchaseResponse Purchase,
    PurchaseValidationDto? Validation = null,
    PurchaseGstSummaryDto? Gst = null
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

public record SiteProductMachineMdbCodeDto(long MachineId, string MachineLabel, int? MdbCode);

public record SiteProductDto(
    long ProductId,
    string Name,
    decimal? AverageUnitCost,
    decimal SitePrice,
    decimal? EstimatedCardProfit,
    int QuantityInStock,
    int MaxStock,
    int? MdbCode,
    List<SiteProductMachineMdbCodeDto> MachineMdbCodes
);
