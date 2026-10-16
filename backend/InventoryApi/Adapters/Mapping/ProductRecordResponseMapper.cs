using Inventory.Application.Machines;
using Inventory.Application.Products;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="ProductRecord"/> onto the API-owned
/// <see cref="ProductResponse"/> the product endpoints serialize (issue #303). It replaces the step
/// that used to rebuild the EF <c>Inventory.Infrastructure.Models.Product</c> entity for those endpoints, and
/// copies only persisted facts: the derived reorder values are computed by the response itself from
/// <c>Inventory.Domain.Products.ProductReorderPolicy</c>, so this mapping cannot introduce a second
/// copy of a reorder formula.
///
/// Since issue #302 the machine-product view is projected here too, through the
/// <see cref="ToResponse(MachineProductRecord, FinancialFigureVisibility)"/> overload below, so the
/// two endpoints that serve a product cannot drift apart: the catalogue shape is built once and the
/// machine slot's own values are overlaid on it.
///
/// <para>Since issue #698 both projections depend on <see cref="FinancialFigureVisibility"/>:
/// <see cref="ProductResponse.InventoryValue"/> is the value of the business's stock at cost - the
/// figure the Dashboard withholds (issue #527) and <c>inventory-value-summary</c> refuses
/// (issue #681), recoverable by summing this one - so a caller who may not be shown it is answered
/// <c>null</c>. Unit cost, the catalogue selling price and the machine slot's own price are not
/// financial figures by that decision and are unchanged for every role.</para>
/// </summary>
public static class ProductRecordResponseMapper
{
    public static ProductResponse ToResponse(ProductRecord record, FinancialFigureVisibility visibility) =>
        visibility == FinancialFigureVisibility.Visible
            ? Full(record)
            : Full(record) with { InventoryValue = null };

    /// <summary>
    /// One slot in a machine's product listing (issue #302): the catalogue product it dispenses,
    /// with the machine's own price, raw Nayax commission metadata, MDB code, slot capacity and
    /// resolved suggested pricing overlaid, and with the slot's stock replacing the product's
    /// storage stock - the same overlay, in the same order, the retired <c>MachineService</c>
    /// applied to the entity. The slot stock override is why the derived reorder values the response
    /// computes describe this machine slot rather than the storage shelf, exactly as before.
    /// </summary>
    public static ProductResponse ToResponse(
        MachineProductRecord record, FinancialFigureVisibility visibility) =>
        ToResponse(record.Product, visibility) with
        {
            QuantityInStock = record.QuantityInStock,
            MachineSlot = new MachineSlotOverlay(
                record.MachinePrice,
                record.CommissionValue,
                record.SuggestedNetValue,
                record.SuggestedPriceValue,
                record.MdbCode,
                record.MaxStockInMachine),
        };

    private static ProductResponse Full(ProductRecord record) => new()
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
