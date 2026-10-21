using System.Text.Json.Serialization;

// Imported for StockAdjustmentDto.Reason only - the temporary compatibility exception documented on
// that record (issue #305). No other DTO in this file names the persistence model.
using Inventory.Infrastructure.Models;
// The GST classification a purchase line carries is a Domain vocabulary type, not a persistence one.
using Inventory.Domain.Gst;

namespace InventoryApi.DTOs;

/// <summary>
/// The product a client posts to <c>POST /api/products</c> (issue #736, parent #329 decision P2).
/// The record itself predates the route: products only ever arrived through the Nayax catalogue
/// import, so a business without a sales provider had no catalogue at all.
///
/// <para><c>CategoryId</c> is a <c>long</c>, like the category key it names: since issue #735 a
/// category's id is assigned by the database, so it is not bounded by the Nayax group ids the
/// previous <c>int</c> happened to fit. Widening a request field accepts every value it accepted
/// before.</para>
///
/// <para>No owning business, and no provider id: the business comes from the caller's membership
/// (AGENTS.md § Tenant ownership and data isolation), and <c>ExternalProductId</c> is written only
/// by a provider import or the catalogue review (issue #738).</para>
///
/// <para><strong>And no supplier since issue #787</strong> (A1 of #786): a product carries no
/// supplier metadata, so <c>supplierId</c> is gone from this request. A submitted
/// <c>supplierId</c> is simply not bound - the create succeeds and nothing is stored for it.</para>
///
/// <para><strong>And no selling price since issue #792</strong> (A2 of #786), on the same terms: a
/// submitted <c>unitPrice</c> is not bound and nothing is stored for it.
/// <see cref="InitialUnitCost"/> is a cost, not a price, and is unaffected.</para>
/// </summary>
public record ProductCreateDto(
    string Name,
    string? Sku,
    string? Description,
    int QuantityInStock,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    long? CategoryId,
    bool IsActive,
    decimal? InitialUnitCost = null
);

/// <summary>
/// The product a client puts to <c>PUT /api/products/{id}</c>.
///
/// <para>Since issue #736 the action binds <c>Name</c> and <c>CategoryId</c> as well, which this
/// record always carried and the action ignored. For a product the business's sales provider names,
/// a <em>change</em> to the name or the category is refused with <c>400</c> and a message naming
/// that provider; sending the stored values back is not a change, and every other field here stays
/// editable for every product.</para>
///
/// <para><c>supplierId</c> is gone since issue #787 (A1 of #786), and <c>unitPrice</c> since issue
/// #792 (A2 of #786), for the same reason each left <see cref="ProductCreateDto"/>: a product has
/// neither to change.</para>
/// </summary>
public record ProductUpdateDto(
    string Name,
    string? Sku,
    string? Description,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    long? CategoryId,
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

/// <summary>
/// The category a client posts to <c>POST /api/categories</c> or puts to
/// <c>PUT /api/categories/{id}</c> (issue #736). A category has nothing else: its provider id is
/// written only by a provider import or the catalogue review (issue #738), and its products point at
/// it rather than being listed here.
/// </summary>
public record CategoryDto(string Name, string? Description);

/// <summary>
/// The wire shape of a category: on <c>/api/categories</c>, and as the nested <c>category</c> of a
/// product.
///
/// <para><see cref="ProviderManagedFields"/> (issue #736) names the fields the business's sales
/// provider manages on this category - <c>["name"]</c> for a category the provider named, an empty
/// array for a local one - so a client locks exactly the inputs the server will refuse. It is
/// <see langword="null"/>, and then omitted from the JSON entirely, wherever a category is carried
/// as part of another resource: the nested <c>category</c> of a product is a snapshot of a pinned
/// payload (<c>InventoryApi.Tests.DTOs.ProductJsonContractTests</c>) and is not an editing surface,
/// so it says nothing about locks rather than saying "none".</para>
/// </summary>
public record CategoryResponse(
    long Id,
    string Name,
    string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ProviderManagedFields = null);

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

// One row of GET /api/sites. The three revenue figures are nullable since issue #697: a caller
// without Dashboard.ViewFinancials - today an Operator - is sent null for each of them, never 0,
// and GetSiteSummaries does not read the site's completed sales for that caller at all. Null here
// means "not this caller's to see"; a period a caller may see that holds no completed sale is an
// honest 0. Every other field, the route and the status codes are unchanged, and a Manager's and an
// Owner's response is what it always was.
public record SiteSummaryDto(
    long SiteId,
    string SiteName,
    int MachineCount,
    decimal TotalStockPercentage,
    int LowProductCount,
    int EmptyProductCount,
    decimal? TodayRevenue,
    decimal? CurrentWeekRevenue,
    decimal? PreviousComparableWeekRevenue
);

public record SiteProductMachineMdbCodeDto(long MachineId, string MachineLabel, int? MdbCode);

// One row of GET /api/sites/{id}/products. EstimatedCardProfit was already nullable for an estimate
// that cannot be made, and since issue #697 it is also null for a caller without
// Dashboard.ViewFinancials; AverageUnitCost and SitePrice are deliberately never withheld. See
// Inventory.Application.Sites.SiteProductRecord for both cases.
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
