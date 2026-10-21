namespace Inventory.Application.Products;

/// <summary>
/// A new product's fields, as seen by the Application layer.
///
/// <para>No supplier since issue #787 (A1 of #786): a product carries no supplier metadata, so
/// there is nothing for a create to set. A supplier is chosen on the purchase or the supplier order
/// instead.</para>
///
/// <para>No selling price since issue #792 (A2 of #786) either: a product carries none, so a create
/// sets none. <see cref="InitialUnitCost"/> is a <em>cost</em> and is unaffected.</para>
/// </summary>
public sealed record ProductCreateFields(
    string Name,
    string? Sku,
    string? Description,
    int QuantityInStock,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    long? CategoryId,
    bool IsActive,
    decimal? InitialUnitCost);
