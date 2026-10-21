namespace Inventory.Application.Products;

/// <summary>
/// A new product's fields, as seen by the Application layer.
///
/// <para>No supplier since issue #787 (A1 of #786): a product carries no supplier metadata, so
/// there is nothing for a create to set. A supplier is chosen on the purchase or the supplier order
/// instead.</para>
/// </summary>
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
    bool IsActive,
    decimal? InitialUnitCost);
