using Inventory.Application.Products;
using InventoryApi.DTOs;
using InventoryApi.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="ProductRecord"/> onto the API-owned
/// <see cref="ProductResponse"/> the product endpoints serialize (issue #303). It replaces the step
/// that used to rebuild the EF <c>InventoryApi.Models.Product</c> entity for those endpoints, and
/// copies only persisted facts: the derived reorder values are computed by the response itself from
/// <c>Inventory.Domain.Products.ProductReorderPolicy</c>, so this mapping cannot introduce a second
/// copy of a reorder formula.
///
/// The machine-product view keeps the entity-shaped response and its own
/// <see cref="ProductResponseMapper"/> until issue #302 migrates it; nothing here is shared with it.
/// </summary>
public static class ProductRecordResponseMapper
{
    public static ProductResponse ToResponse(ProductRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Sku = record.Sku,
        Description = record.Description,
        UnitPrice = record.UnitPrice,
        AverageUnitCost = record.AverageUnitCost,
        CostingQuantity = record.CostingQuantity,
        InventoryValue = record.InventoryValue,
        QuantityInStock = record.QuantityInStock,
        LowStockThreshold = record.LowStockThreshold,
        RestockTo = record.RestockTo,
        Unit = record.Unit,
        IsActive = record.IsActive,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        CategoryId = record.CategoryId,
        Category = record.Category is null
            ? null
            : new CategoryResponse(record.Category.Id, record.Category.Name, record.Category.Description),
        SupplierId = record.SupplierId,
        Supplier = record.Supplier is null
            ? null
            : new SupplierResponse(
                record.Supplier.Id,
                record.Supplier.Name,
                record.Supplier.ContactName,
                record.Supplier.Phone,
                record.Supplier.Email,
                record.Supplier.Address),
        StockAdjustments = record.StockAdjustments.Select(adjustment => new ProductStockAdjustmentResponse(
            adjustment.Id,
            adjustment.ProductId,
            adjustment.ReceiptItemId,
            adjustment.QuantityChange,
            adjustment.QuantityAfter,
            adjustment.UnitCost,
            adjustment.TotalCost,
            adjustment.CostingQuantityAfter,
            adjustment.AverageUnitCostAfter,
            adjustment.InventoryValueAfter,
            (StockAdjustmentReason)adjustment.Reason,
            (StockAdjustmentSource)adjustment.Source,
            adjustment.MachineId,
            adjustment.Notes,
            adjustment.EatBefore,
            adjustment.CreatedAt,
            adjustment.EffectiveAt)).ToList(),

        // Resolved live by the reorder-alert listing, not persisted; zero on the read paths that do
        // not resolve them, exactly as before. The derived values follow from these.
        MachineReplenishmentNeed = record.MachineReplenishmentNeed,
        OnOrderQuantity = record.OnOrderQuantity,
    };
}
