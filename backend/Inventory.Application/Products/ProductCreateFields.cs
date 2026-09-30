namespace Inventory.Application.Products;

/// <summary>A new product's fields, as seen by the Application layer.</summary>
public sealed record ProductCreateFields(
    string Name,
    string? Sku,
    string? Description,
    decimal UnitPrice,
    int QuantityInStock,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    long? CategoryId,
    int? SupplierId,
    bool IsActive,
    decimal? InitialUnitCost);
