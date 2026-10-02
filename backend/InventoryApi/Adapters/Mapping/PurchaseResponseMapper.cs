using Inventory.Application.Purchases;
using InventoryApi.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Maps the Application layer's purchase read models back onto the <see cref="Purchase"/> shape
/// the purchase endpoints have always serialised (issue #281): every key and nesting level a
/// client already receives is reproduced here, including a product/supplier navigation being
/// absent exactly when the former service never loaded it (a newly uploaded purchase's line items,
/// for example, carry no <see cref="PurchaseItem.Product"/>).
///
/// The returned instances are detached response objects, never attached to a
/// <see cref="Data.AppDbContext"/>. Replacing them with a dedicated response DTO belongs with
/// deleting the remaining legacy delegators (issue #153), not with this slice, which must keep the
/// contract byte-for-byte identical.
/// </summary>
internal static class PurchaseResponseMapper
{
    public static Purchase ToPurchase(PurchaseRecord record) => new()
    {
        BusinessId = record.BusinessId,
        Id = record.Id,
        Title = record.Title,
        Notes = record.Notes,
        TotalAmount = record.TotalAmount,
        DeliveryCost = record.DeliveryCost,
        PackageCost = record.PackageCost,
        PurchaseDate = record.PurchaseDate,
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
        Items = record.Items.Select(ToPurchaseItem).ToList(),
        FileName = record.FileName,
        StoredFileName = record.StoredFileName,
        ContentType = record.ContentType,
        FileSizeBytes = record.FileSizeBytes,
        CreatedAt = record.CreatedAt,
    };

    private static PurchaseItem ToPurchaseItem(PurchaseItemRecord record) => new()
    {
        Id = record.Id,
        ReceiptId = record.ReceiptId,
        ProductId = record.ProductId,
        Quantity = record.Quantity,
        UnitCost = record.UnitCost,
        Product = record.Product is null ? null : ToProduct(record.Product),
    };

    private static Product ToProduct(PurchaseProductSummaryRecord record) => new()
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
