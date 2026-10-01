using Inventory.Application.Machines;
using Inventory.Application.Products;
using InventoryApi.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Maps the Application layer's product read models back onto the <see cref="Product"/> shape the
/// product and machine-product endpoints have always serialised (issue #240). It exists so the
/// migration could move the orchestration without changing the public response contract: every key,
/// nesting level and derived value a client already receives is reproduced here, and the derived
/// reorder values come from the same <see cref="Inventory.Domain.Products.ProductReorderPolicy"/> the
/// use cases ranked and filtered with.
///
/// The returned instances are detached response objects, never attached to a
/// <see cref="Data.AppDbContext"/>. Replacing them with a dedicated response DTO belongs with
/// deleting the remaining legacy delegators (issue #153), not with this slice, which must keep the
/// contract byte-for-byte identical.
/// </summary>
internal static class ProductResponseMapper
{
    public static Product ToProduct(ProductRecord record) => new()
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
            : new Category
            {
                Id = record.Category.Id,
                Name = record.Category.Name,
                Description = record.Category.Description,
            },
        SupplierId = record.SupplierId,
        Supplier = record.Supplier is null
            ? null
            : new Supplier
            {
                Id = record.Supplier.Id,
                Name = record.Supplier.Name,
                ContactName = record.Supplier.ContactName,
                Phone = record.Supplier.Phone,
                Email = record.Supplier.Email,
                Address = record.Supplier.Address,
            },
        StockAdjustments = record.StockAdjustments.Select(adjustment => new StockAdjustment
        {
            Id = adjustment.Id,
            ProductId = adjustment.ProductId,
            ReceiptItemId = adjustment.ReceiptItemId,
            QuantityChange = adjustment.QuantityChange,
            QuantityAfter = adjustment.QuantityAfter,
            UnitCost = adjustment.UnitCost,
            TotalCost = adjustment.TotalCost,
            CostingQuantityAfter = adjustment.CostingQuantityAfter,
            AverageUnitCostAfter = adjustment.AverageUnitCostAfter,
            InventoryValueAfter = adjustment.InventoryValueAfter,
            Reason = (StockAdjustmentReason)adjustment.Reason,
            Source = (StockAdjustmentSource)adjustment.Source,
            MachineId = adjustment.MachineId,
            Notes = adjustment.Notes,
            EatBefore = adjustment.EatBefore,
            CreatedAt = adjustment.CreatedAt,
            EffectiveAt = adjustment.EffectiveAt,
        }).ToList(),

        // Resolved live by the reorder use case, not persisted; zero on the read paths that do not
        // resolve them, exactly as before. The derived NeedToOrder/IsReorderAlert/IsLowStock values
        // follow from these through ProductReorderPolicy.
        MachineReplenishmentNeed = record.MachineReplenishmentNeed,
        OnOrderQuantity = record.OnOrderQuantity,
    };

    /// <summary>
    /// The machine-slot view: the catalogue product with the machine's own price, raw Nayax
    /// commission metadata, MDB code, slot stock, slot capacity and suggested pricing overlaid.
    /// </summary>
    public static Product ToProduct(MachineProductRecord record)
    {
        var product = ToProduct(record.Product);
        product.MachinePrice = record.MachinePrice;
        product.CommissionValue = record.CommissionValue;
        product.MdbCode = record.MdbCode;
        product.QuantityInStock = record.QuantityInStock;
        product.MaxStockInMachine = record.MaxStockInMachine;
        product.SuggestedNetValue = record.SuggestedNetValue;
        product.SuggestedPriceValue = record.SuggestedPriceValue;
        return product;
    }
}
