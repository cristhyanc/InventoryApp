using InventoryApi.Models;

namespace InventoryApi.DTOs;

/// <summary>
/// One movement on the global Stock History endpoint <c>GET /api/stock-history</c> (issue #384).
///
/// It is an API-owned contract: no EF entity is serialized, following the pattern issue #305
/// established for the product-specific stock endpoints. It is deliberately a different shape from
/// <see cref="ProductStockAdjustmentResponse"/> rather than a reuse of it, for two reasons:
/// <list type="bullet">
///   <item>a cross-product listing has to name the product on every row, so
///   <see cref="ProductName"/> is part of the contract - the alternative is a client that fans out a
///   product lookup per row, which this endpoint exists to avoid; and</item>
///   <item><c>effectiveAt</c> is absent. This query orders and filters on
///   <see cref="CreatedAt"/> (issue #384 fixes that explicitly), and the costing effective instant
///   is not something this page presents, so it is not published here. The product-specific
///   endpoints keep publishing it, unchanged.</item>
/// </list>
///
/// <see cref="Reason"/>/<see cref="Source"/> are the <c>InventoryApi.Models</c> enums, like the
/// other stock contracts: the published document already reaches those CLR enums from the pinned
/// legacy components, and a second enum of the same simple name makes Swashbuckle fail document
/// generation with a duplicate schema id. See <c>InventoryApi.DTOs.ProductStockAdjustmentResponse</c>
/// and <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> for that compatibility exception;
/// using the same vocabulary also means a client switches on the same numeric values everywhere.
///
/// <see cref="CreatedAt"/> is the persisted UTC instant, serialized with its UTC identity intact
/// (<c>AppDbContext</c> re-specifies <see cref="DateTimeKind.Utc"/> on read - see issue #230), which
/// is what lets the frontend render it in business time.
/// </summary>
public sealed record StockHistoryEntryResponse(
    int Id,
    long ProductId,
    string ProductName,
    int? ReceiptItemId,
    int QuantityChange,
    int QuantityAfter,
    decimal? UnitCost,
    decimal? TotalCost,
    int? CostingQuantityAfter,
    decimal? AverageUnitCostAfter,
    decimal? InventoryValueAfter,
    StockAdjustmentReason Reason,
    StockAdjustmentSource Source,
    long? MachineId,
    string? Notes,
    DateTime? EatBefore,
    DateTime CreatedAt);

/// <summary>
/// One bounded page of the global Stock History (issue #384). The envelope is part of the contract:
/// a stock-adjustment history grows without limit, so the endpoint never answers "everything".
///
/// <see cref="Page"/> and <see cref="PageSize"/> are the values the server actually served, which
/// may differ from the requested ones - <c>Inventory.Application.Stock.StockHistoryPaging</c> clamps
/// a page size above the server maximum rather than failing the request, so a client can tell what
/// it got. <see cref="TotalCount"/> counts every movement matching the filter, not the rows in
/// <see cref="Items"/>.
/// </summary>
public sealed record StockHistoryPageResponse(
    IReadOnlyList<StockHistoryEntryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);
