using Inventory.Application.SupplierOrders;
using InventoryApi.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Maps the Application layer's supplier-order read models back onto the
/// <see cref="SupplierOrder"/> shape the supplier-order endpoints have always serialised (issue
/// #281). <see cref="SupplierOrderLine.OutstandingQuantity"/> is a computed property that reads the
/// line's own <see cref="SupplierOrderLine.SupplierOrder"/> navigation (for its cancelled check),
/// so each reconstructed line is wired back to its reconstructed parent order even though that
/// navigation is <c>[JsonIgnore]</c>d - otherwise the computed value would be wrong rather than
/// merely unserialised.
///
/// The returned instances are detached response objects, never attached to a
/// <see cref="Data.AppDbContext"/>. Replacing them with a dedicated response DTO belongs with
/// deleting the remaining legacy delegators (issue #153), not with this slice, which must keep the
/// contract byte-for-byte identical.
/// </summary>
internal static class SupplierOrderResponseMapper
{
    public static SupplierOrder ToSupplierOrder(SupplierOrderRecord record)
    {
        var order = new SupplierOrder
        {
            Id = record.Id,
            BusinessId = record.BusinessId,
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
            OrderDate = record.OrderDate,
            ExpectedDate = record.ExpectedDate,
            Reference = record.Reference,
            Notes = record.Notes,
            Status = (SupplierOrderStatus)record.Status,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };
        order.Lines = record.Lines.Select(line => ToLine(line, order)).ToList();
        return order;
    }

    private static SupplierOrderLine ToLine(SupplierOrderLineRecord record, SupplierOrder order) => new()
    {
        Id = record.Id,
        SupplierOrderId = record.SupplierOrderId,
        SupplierOrder = order,
        ProductId = record.ProductId,
        Product = ToProduct(record.Product),
        QuantityOrdered = record.QuantityOrdered,
        QuantityReceived = record.QuantityReceived,
        UnitPrice = record.UnitPrice,
        Notes = record.Notes,
    };

    private static Product ToProduct(SupplierOrderProductSummaryRecord record) => new()
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
