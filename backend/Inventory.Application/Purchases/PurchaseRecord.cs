namespace Inventory.Application.Purchases;

/// <summary>
/// One purchase as the Application layer sees it: its fields, its supplier detail, its line
/// items, and the stored-document metadata for its supporting scan/photo.
/// </summary>
public sealed record PurchaseRecord(
    int Id,
    int BusinessId,
    string Title,
    string? Notes,
    decimal? TotalAmount,
    decimal? DeliveryCost,
    decimal? PackageCost,
    DateTime PurchaseDate,
    int? SupplierId,
    PurchaseSupplierRecord? Supplier,
    IReadOnlyList<PurchaseItemRecord> Items,
    string FileName,
    string StoredFileName,
    string ContentType,
    long FileSizeBytes,
    DateTime CreatedAt);

/// <summary>The supplier detail carried on a purchase, as the Application layer sees it.</summary>
public sealed record PurchaseSupplierRecord(
    int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address);

/// <summary>One persisted purchase line item, with its product's catalogue snapshot.</summary>
public sealed record PurchaseItemRecord(
    int Id, int ReceiptId, long ProductId, decimal Quantity, decimal UnitCost, PurchaseProductSummaryRecord? Product);

/// <summary>
/// The catalogue snapshot a purchase line item's product navigation carries. It mirrors only the
/// persisted scalar fields the former <c>Include(i => i.Product)</c> query loaded - never the
/// product's own category/supplier/stock-adjustment history, which that query never included
/// either - so the InventoryApi response mapper can reconstruct the exact response shape clients
/// already receive.
/// </summary>
public sealed record PurchaseProductSummaryRecord(
    long Id,
    string Name,
    string? Sku,
    string? Description,
    decimal UnitPrice,
    decimal AverageUnitCost,
    int? CostingQuantity,
    decimal? InventoryValue,
    int QuantityInStock,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long? CategoryId,
    int? SupplierId);

/// <summary>The stored-document metadata needed to open, persist, or delete a purchase's supporting document.</summary>
public sealed record PurchaseFileMetadata(string StoredFileName, string ContentType, string FileName, long FileSizeBytes);
