using Inventory.Domain.Products;
using InventoryApi.Models;

namespace InventoryApi.DTOs;

/// <summary>
/// The wire shape of a product on the "/api/products" endpoints (issue #303), replacing the EF
/// <c>InventoryApi.Models.Product</c> entity the controller used to serialize directly. Key names, order,
/// nesting and values match that entity's serializable surface exactly - including the fields the
/// catalogue endpoints have always emitted at their defaults - so this is not a contract change;
/// <c>InventoryApi.Tests.DTOs.ProductJsonContractTests</c> compares the serialized bytes of both.
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

    // The machine-slot fields of the entity this response replaced. Only the machine-product view
    // ever overlaid them, so every "/api/products" response has always carried them at these
    // defaults; they are reproduced as constants to keep that response byte-identical. The
    // machine-product view keeps the entity shape until issue #302 migrates it, and is not served
    // from here.
    public decimal MachinePrice => 0m;
    public decimal CommissionValue => 0m;
    public decimal? SuggestedNetValue => null;
    public decimal? SuggestedPriceValue => null;
    public int? MdbCode => null;
    public int? MaxStockInMachine => null;

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
/// One stock movement in a product's history, as the product endpoints serialize it. Matches the
/// serializable surface of the <c>InventoryApi.Models.StockAdjustment</c> entity it replaced: the owning
/// business, the product back-reference and the receipt-item navigation stay off the wire, and
/// <see cref="Reason"/>/<see cref="Source"/> keep the persisted numeric values.
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
