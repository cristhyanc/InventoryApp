using Inventory.Domain.Products;

namespace Inventory.Application.Products;

/// <summary>
/// One catalogue product as the Application layer sees it (issue #240): the persisted catalogue and
/// costing fields, its category/supplier detail, its stock-adjustment history, and the two reorder
/// inputs (<see cref="MachineReplenishmentNeed"/>/<see cref="OnOrderQuantity"/>) that
/// <see cref="ListLowStockProducts"/> resolves live rather than reading from the database.
///
/// Derived reorder values are deliberately absent: <see cref="Inventory.Domain.Products.ProductReorderPolicy"/>
/// owns those formulas and both this layer and the API's response mapping call it, so there is one
/// implementation rather than a cached copy that can disagree with it.
/// </summary>
public sealed record ProductRecord
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Sku { get; init; }
    public string? Description { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal AverageUnitCost { get; init; }
    public int? CostingQuantity { get; init; }
    public decimal? InventoryValue { get; init; }
    public required int QuantityInStock { get; init; }
    public required int LowStockThreshold { get; init; }
    public required int RestockTo { get; init; }
    public string? Unit { get; init; }
    public required bool IsActive { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public long? CategoryId { get; init; }
    public ProductCategoryRecord? Category { get; init; }
    public int? SupplierId { get; init; }
    public ProductSupplierRecord? Supplier { get; init; }
    public IReadOnlyList<ProductStockAdjustmentRecord> StockAdjustments { get; init; } = [];

    /// <summary>
    /// Units the machines currently need refilled from storage. Zero unless a caller resolved the
    /// live machine fleet (<see cref="ListLowStockProducts"/>), exactly as the former
    /// <c>ProductService.GetAll</c>/<c>Get</c> left it unset.
    /// </summary>
    public int MachineReplenishmentNeed { get; init; }

    /// <summary>Units on an outstanding supplier order, resolved on the same path.</summary>
    public decimal OnOrderQuantity { get; init; }

    /// <summary>The reorder quantity for this product, from the one authoritative Domain formula.</summary>
    public decimal NeedToOrder => ProductReorderPolicy.NeedToOrder(
        QuantityInStock, OnOrderQuantity, MachineReplenishmentNeed, LowStockThreshold, RestockTo);

    /// <summary>Whether this product belongs on the reorder-alert list, from the same Domain formula.</summary>
    public bool IsReorderAlert => ProductReorderPolicy.IsReorderAlert(IsActive, NeedToOrder);
}

/// <summary>The category detail carried on a product, as the Application layer sees it.</summary>
public sealed record ProductCategoryRecord(long Id, string Name, string? Description);

/// <summary>The supplier detail carried on a product, as the Application layer sees it.</summary>
public sealed record ProductSupplierRecord(
    int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address);

/// <summary>
/// One persisted stock movement in a product's history. <paramref name="Reason"/> and
/// <paramref name="Source"/> are the persisted reason/source codes, passed through unchanged: the
/// stock-movement rules and their enum vocabulary have not migrated out of the persistence model yet
/// (docs/architecture.md backend migration track item 6), and no product read orchestration branches
/// on them.
/// </summary>
public sealed record ProductStockAdjustmentRecord(
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
    int Reason,
    int Source,
    long? MachineId,
    string? Notes,
    DateTime? EatBefore,
    DateTime CreatedAt,
    DateTime EffectiveAt);
