namespace Inventory.Application.Products;

/// <summary>
/// A product's editable fields, as seen by the Application layer. <c>Name</c>, <c>UnitPrice</c>, and
/// <c>CategoryId</c> are deliberately absent: they are Nayax-managed/read-only through this use case,
/// exactly as the former <c>InventoryApi.Services.ProductService.Update</c> left them untouched.
/// </summary>
public sealed record ProductUpdateFields(
    string? Sku,
    string? Description,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    int? SupplierId,
    bool IsActive);
