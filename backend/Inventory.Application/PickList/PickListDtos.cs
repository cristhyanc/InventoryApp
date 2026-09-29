namespace Inventory.Application.PickList;

/// <summary>
/// One machine's current/target/pick figures for a product in the Pick List matrix (issue #221),
/// aggregated across however many MDB slots that product occupies on the machine - the same
/// duplicate-mapping aggregation <see cref="Inventory.Application.Reorder.CalculateReorderNeeds"/>
/// already applies when summing <c>MissingStockByMDB</c>. <see cref="CurrentQuantity"/> and
/// <see cref="TargetQuantity"/> reuse the exact PAR/MissingStockByMDB arithmetic
/// <c>InventoryApi.Services.MachineService.GetMachineProducts</c> already uses to show a machine's
/// per-product stock and restock target; <see cref="QuantityToPick"/> is the same
/// <c>MissingStockByMDB</c> value clamped to zero, so a machine already at or above its target never
/// asks for a pick.
/// </summary>
public record PickListMachineQuantity(
    long MachineId,
    int CurrentQuantity,
    int TargetQuantity,
    int QuantityToPick
);

/// <summary>
/// One product row of the Pick List matrix: its physical storage quantity (the authoritative
/// <c>Product.QuantityInStock</c> home-stock figure, untouched by this read-only query), the total
/// quantity to pick across the selected machines, and whether that total exceeds what is physically
/// on the shelf.
/// </summary>
public record PickListProduct(
    long ProductId,
    string ProductName,
    int StorageQuantityInStock,
    int TotalQuantityToPick,
    int StorageShortageQuantity,
    IReadOnlyList<PickListMachineQuantity> MachineQuantities
);

/// <summary>The complete read-only Pick List projection for the requested machines (issue #221).</summary>
public record PickListResult(IReadOnlyList<PickListProduct> Products);
