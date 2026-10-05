using System.Text.Json.Serialization;

using Inventory.Domain.Products;
using Inventory.Infrastructure.Models;

namespace InventoryApi.DTOs;

/// <summary>
/// The machine-slot facts a machine's product listing overlays on the catalogue product that slot
/// dispenses (issue #302): the machine's own live retail price, the raw Nayax commission metadata,
/// the MDB code, the slot capacity, and the suggested net/retail values
/// <c>Inventory.Application.Products.ResolveMachineProductPricing</c> resolved for it. The slot's
/// own stock is not here - it overrides <see cref="ProductResponse.QuantityInStock"/> itself, as it
/// always has.
///
/// <see cref="None"/> is the catalogue view, where the API has always emitted these six fields at
/// exactly these defaults. Carrying them in one ignored member rather than as six settable members
/// is what keeps the published <c>ProductResponse</c> schema identical: Swashbuckle describes a
/// property with no setter as <c>readOnly</c>, and all six have been published that way.
/// </summary>
public sealed record MachineSlotOverlay(
    decimal MachinePrice,
    decimal CommissionValue,
    decimal? SuggestedNetValue,
    decimal? SuggestedPriceValue,
    int? MdbCode,
    int? MaxStockInMachine)
{
    public static MachineSlotOverlay None { get; } = new(0m, 0m, null, null, null, null);
}

/// <summary>
/// The wire shape of a product on the "/api/products" endpoints (issue #303) and, since issue #302,
/// on "/api/machines/{id}/products", replacing the EF
/// <c>Inventory.Infrastructure.Models.Product</c> entity the controllers used to serialize directly. Key names, order,
/// nesting and values match that entity's serializable surface exactly - including the fields the
/// catalogue endpoints have always emitted at their defaults - so this is not a contract change;
/// <c>InventoryApi.Tests.DTOs.ProductJsonContractTests</c> and
/// <c>InventoryApi.Tests.DTOs.MachineJsonContractTests</c> compare the serialized bytes of both
/// views against that entity.
///
/// The derived reorder values stay derived here rather than being carried as data, so they can only
/// ever come from <see cref="ProductReorderPolicy"/> - the same authoritative formulas
/// <c>Inventory.Application.Products.ListLowStockProducts</c> selects and ranks the alerts with, per
/// AGENTS.md's "one authoritative calculation must feed all presentations".
/// </summary>
public sealed record ProductResponse
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Sku { get; init; }
    public string? Description { get; init; }

    /// <summary>The catalog default/list selling price, synced one-way from Nayax; never a cost.</summary>
    public required decimal UnitPrice { get; init; }

    public required decimal AverageUnitCost { get; init; }
    public int? CostingQuantity { get; init; }
    public decimal? InventoryValue { get; init; }

    /// <summary>
    /// The machine slot this response describes, when it describes one (issue #302). Never
    /// serialized: the six fields below publish it in the entity's own positions and spellings, and
    /// <see cref="MachineSlotOverlay.None"/> is the catalogue view every "/api/products" response
    /// has always emitted.
    /// </summary>
    [JsonIgnore]
    public MachineSlotOverlay MachineSlot { get; init; } = MachineSlotOverlay.None;

    // The machine-slot fields of the entity this response replaced, kept derived so a catalogue
    // response cannot carry a machine's price by accident and the published schema keeps describing
    // all six as readOnly.
    public decimal MachinePrice => MachineSlot.MachinePrice;
    public decimal CommissionValue => MachineSlot.CommissionValue;
    public decimal? SuggestedNetValue => MachineSlot.SuggestedNetValue;
    public decimal? SuggestedPriceValue => MachineSlot.SuggestedPriceValue;
    public int? MdbCode => MachineSlot.MdbCode;
    public int? MaxStockInMachine => MachineSlot.MaxStockInMachine;

    /// <summary>
    /// Units the machines currently need refilled from storage. Zero unless the reorder-alert
    /// listing resolved the live machine fleet, exactly as on the entity this replaced.
    /// </summary>
    public int MachineReplenishmentNeed { get; init; }

    /// <summary>Units on an outstanding supplier order, resolved on the same path.</summary>
    public decimal OnOrderQuantity { get; init; }

    public decimal ProjectedStockForReorder => ProductReorderPolicy.ProjectedStockForReorder(
        QuantityInStock, OnOrderQuantity, MachineReplenishmentNeed);

    public required int QuantityInStock { get; init; }
    public required int LowStockThreshold { get; init; }
    public required int RestockTo { get; init; }

    public decimal NeedToOrder => ProductReorderPolicy.NeedToOrder(
        QuantityInStock, OnOrderQuantity, MachineReplenishmentNeed, LowStockThreshold, RestockTo);

    public string? Unit { get; init; }
    public required bool IsActive { get; init; }

    // Never populated on any read path, on the entity either; kept so the response shape is unchanged.
    public DateTime? LastEatBefore1 => null;
    public DateTime? LastEatBefore2 => null;

    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }

    public long? CategoryId { get; init; }
    public CategoryResponse? Category { get; init; }

    public int? SupplierId { get; init; }
    public SupplierResponse? Supplier { get; init; }

    public IReadOnlyList<ProductStockAdjustmentResponse> StockAdjustments { get; init; } = [];

    public bool IsLowStock => ProductReorderPolicy.IsLowStock(QuantityInStock, LowStockThreshold);

    public bool IsReorderAlert => ProductReorderPolicy.IsReorderAlert(IsActive, NeedToOrder);
}

/// <summary>
/// One stock movement in a product's history, as the product endpoints serialize it and, since
/// issue #305, as the "/api/products/{productId}/stock" history and adjust endpoints serialize it
/// too - one wire shape for a stock movement, not two. Matches the
/// serializable surface of the <c>Inventory.Infrastructure.Models.StockAdjustment</c> entity it replaced: the owning
/// business, the product back-reference and the receipt-item navigation stay off the wire, and
/// <see cref="Reason"/>/<see cref="Source"/> keep the persisted numeric values.
///
/// The stock endpoints publish this response under the <c>StockAdjustment</c> schema id they have
/// always published, which <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> substitutes
/// for their response; the product endpoints keep publishing it under its own id as the item type of
/// <see cref="ProductResponse.StockAdjustments"/>. The two published components are schema-identical,
/// which <c>InventoryApi.Tests.Swagger.StockAndExpenseSchemaContractTests</c> asserts.
///
/// <see cref="Reason"/>/<see cref="Source"/> are, with the request DTO's
/// <c>StockAdjustmentDto.Reason</c>, the one place an API contract still names the persistence enums
/// - a temporary compatibility exception - because the published document reaches the same CLR enums
/// from both of those pinned components; an API-owned copy under the same simple name makes
/// Swashbuckle fail document generation with a duplicate schema id. See
/// <c>InventoryApi.Adapters.Mapping.StockAdjustmentResponseMapper</c>.
/// </summary>
public sealed record ProductStockAdjustmentResponse(
    int Id,
    long ProductId,
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
    DateTime CreatedAt,
    DateTime EffectiveAt);
