using Inventory.Application.SupplierOrders;
using Inventory.Domain.SupplierOrders;
using InventoryApi.DTOs;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="SupplierOrderRecord"/> onto the API-owned
/// <see cref="SupplierOrderResponse"/> the supplier-order endpoints serialize (issue #304). It
/// replaces the step that used to rebuild the EF <c>InventoryApi.Models.SupplierOrder</c> entity for
/// those endpoints, so nothing here references the persistence model any more.
///
/// Each line carries its parent order's status, which the response needs - and does not serialize -
/// to derive its outstanding quantity through
/// <c>Inventory.Domain.SupplierOrders.SupplierOrderLineOutstandingPolicy</c>. That replaces the
/// entity-shaped mapping's trick of wiring every reconstructed line back to its reconstructed parent
/// so the entity's computed property could reach the same fact through a <c>[JsonIgnore]</c>d
/// navigation.
/// </summary>
public static class SupplierOrderResponseMapper
{
    public static SupplierOrderResponse ToResponse(SupplierOrderRecord record) => new()
    {
        Id = record.Id,
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
        OrderDate = record.OrderDate,
        ExpectedDate = record.ExpectedDate,
        Reference = record.Reference,
        Notes = record.Notes,
        Status = record.Status,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        Lines = record.Lines.Select(line => ToLineResponse(line, record.Status)).ToList(),
    };

    private static SupplierOrderLineResponse ToLineResponse(SupplierOrderLineRecord record, SupplierOrderStatus orderStatus) => new()
    {
        Id = record.Id,
        SupplierOrderId = record.SupplierOrderId,
        ProductId = record.ProductId,
        Product = ToProductResponse(record.Product),
        QuantityOrdered = record.QuantityOrdered,
        QuantityReceived = record.QuantityReceived,
        UnitPrice = record.UnitPrice,
        Notes = record.Notes,
        OrderStatus = orderStatus,
    };

    /// <summary>
    /// The catalogue snapshot a line's product navigation carries. It is the same
    /// <see cref="ProductResponse"/> the product endpoints serialize - as it was the same
    /// <c>Product</c> entity before - so the nested object keeps every key it always had, with the
    /// category/supplier detail and stock history the supplier-order read path never loaded absent
    /// and the unresolved reorder inputs at zero.
    /// </summary>
    private static ProductResponse ToProductResponse(SupplierOrderProductSummaryRecord record) => new()
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
        SupplierId = record.SupplierId,
    };
}
