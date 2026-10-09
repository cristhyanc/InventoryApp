using Inventory.Application.Purchases;
using InventoryApi.DTOs;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="PurchaseRecord"/> onto the API-owned
/// <see cref="PurchaseResponse"/> the purchase endpoints serialize (issue #304). It replaces the
/// step that used to rebuild the EF <c>Inventory.Infrastructure.Models.Purchase</c> entity for those endpoints,
/// so nothing here references the persistence model any more.
///
/// It copies only facts: every key and nesting level a client already receives is reproduced,
/// including a supplier/product navigation being absent exactly when the read path did not load it
/// (a newly uploaded purchase's line items, for example, carry no product), and the line total and
/// the nested product's reorder values stay derived by the response types themselves from their
/// Domain policies rather than being computed a second time here.
/// </summary>
public static class PurchaseResponseMapper
{
    public static PurchaseResponse ToResponse(PurchaseRecord record) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Notes = record.Notes,
        TotalAmount = record.TotalAmount,
        DeliveryCost = record.DeliveryCost,
        DeliveryGstClassification = record.DeliveryGstClassification,
        DeliveryGstClassificationSource = record.DeliveryGstClassificationSource,
        PackageCost = record.PackageCost,
        PackageGstClassification = record.PackageGstClassification,
        PackageGstClassificationSource = record.PackageGstClassificationSource,
        PurchaseDate = record.PurchaseDate,
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
        Items = record.Items.Select(ToItemResponse).ToList(),
        FileName = record.FileName,
        StoredFileName = record.StoredFileName,
        ContentType = record.ContentType,
        FileSizeBytes = record.FileSizeBytes,
        CreatedAt = record.CreatedAt,
    };

    private static PurchaseItemResponse ToItemResponse(PurchaseItemRecord record) => new()
    {
        Id = record.Id,
        ReceiptId = record.ReceiptId,
        ProductId = record.ProductId,
        Product = record.Product is null ? null : ToProductResponse(record.Product),
        Quantity = record.Quantity,
        UnitCost = record.UnitCost,
        GstClassification = record.GstClassification,
        GstClassificationSource = record.GstClassificationSource,
    };

    /// <summary>
    /// The catalogue snapshot a line item's product navigation carries. It is the same
    /// <see cref="ProductResponse"/> the product endpoints serialize - as it was the same
    /// <c>Product</c> entity before - so the nested object keeps every key it always had, with the
    /// category/supplier detail and stock history the purchase read path never loaded absent and the
    /// unresolved reorder inputs at zero.
    /// </summary>
    private static ProductResponse ToProductResponse(PurchaseProductSummaryRecord record) => new()
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
