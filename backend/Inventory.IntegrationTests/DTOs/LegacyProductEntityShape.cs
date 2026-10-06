using Inventory.Application.Machines;
using Inventory.Application.Products;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// The EF <c>Product</c> entity the retired delegators serialised, rebuilt from an Application
/// record exactly as <c>InventoryApi.Adapters.Mapping.ProductResponseMapper</c> did before issues
/// #303 and #302 replaced it with the API-owned <see cref="InventoryApi.DTOs.ProductResponse"/>.
///
/// It is the reference value of the product wire contract, kept in the test project on purpose: the
/// production code must no longer be able to produce it, and a contract test that compared the new
/// response with another copy of the new response would prove nothing. Both the catalogue response
/// (<c>/api/products</c>, issue #303) and the machine-slot response
/// (<c>/api/machines/{id}/products</c>, issue #302) are compared against it, so the two endpoints
/// cannot drift from the one shape they have always shared either.
/// </summary>
internal static class LegacyProductEntityShape
{
    /// <summary>
    /// The catalogue view. The machine-slot fields stay at their defaults, exactly as they did on
    /// every <c>/api/products</c> response, because only the machine-product view overlays them.
    /// </summary>
    public static Product ForCatalogue(ProductRecord record) => new()
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
        MachineReplenishmentNeed = record.MachineReplenishmentNeed,
        OnOrderQuantity = record.OnOrderQuantity,
    };

    /// <summary>
    /// The machine-slot view: the catalogue product with the machine's own price, raw Nayax
    /// commission metadata, MDB code, slot stock, slot capacity and suggested pricing overlaid -
    /// the overlay order and the slot-stock override the retired <c>MachineService</c> applied.
    /// </summary>
    public static Product ForMachineSlot(MachineProductRecord record)
    {
        var product = ForCatalogue(record.Product);
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
