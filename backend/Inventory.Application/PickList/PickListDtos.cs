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
///
/// <see cref="MdbCode"/> is the MDB code for this specific machine-product entry (issue #496), the
/// same <see cref="Inventory.Application.Nayax.NayaxMachineProduct.MDBCode"/> field
/// <see cref="Inventory.Application.Machines.ListMachineProducts"/> already surfaces for one
/// machine. It belongs to this one machine's slot, never the product globally: the same product can
/// legitimately carry a different code on another machine, or the same code again - both stay
/// visible per machine rather than being collapsed into one shared value. When the duplicate-mapping
/// aggregation above merges more than one MDB slot for this product on this machine, this is the
/// first slot's code in the order Nayax returned them, matching how their quantities are already
/// merged rather than kept separate.
/// </summary>
public record PickListMachineQuantity(
    long MachineId,
    int? MdbCode,
    int CurrentQuantity,
    int TargetQuantity,
    int QuantityToPick
);

/// <summary>
/// One product row of the Pick List matrix: its physical storage quantity (the authoritative
/// <c>Product.QuantityInStock</c> home-stock figure, untouched by this read-only query), the total
/// quantity to pick across the selected machines, and whether that total exceeds what is physically
/// on the shelf.
///
/// <see cref="MdbCode"/> is the row's representative MDB code (issue #496): the lowest non-null
/// <see cref="PickListMachineQuantity.MdbCode"/> across <see cref="MachineQuantities"/>, or
/// <see langword="null"/> when none of them has one. It drives this row's position in the default
/// ascending sort and the row-level MDB Code column; it is a display/sort convenience derived from
/// the per-machine codes below, never a second source of truth - a product is not assumed to have
/// one globally unique code, and the per-machine values are what the UI shows for each machine
/// column.
/// </summary>
public record PickListProduct(
    long ProductId,
    string ProductName,
    int? MdbCode,
    int StorageQuantityInStock,
    int TotalQuantityToPick,
    int StorageShortageQuantity,
    IReadOnlyList<PickListMachineQuantity> MachineQuantities
);

/// <summary>The complete read-only Pick List projection for the requested machines (issue #221).</summary>
public record PickListResult(IReadOnlyList<PickListProduct> Products);
