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
///
/// <para><strong>Since issue #1051 a create may also carry the master product the operator picked</strong>
/// (parent #817). Both of the last two values are optional and their defaults are the create every
/// caller made before: no selection, so nothing about the shared price catalogue is read or written
/// and the product is created exactly as it always was.</para>
///
/// <para><see cref="SelectedMasterProductId"/> is the shared master product the operator created
/// this product from, and it is believed only while the submitted <see cref="Name"/> and
/// <see cref="Unit"/> are still that master product's own recomputed values
/// (<c>Inventory.Domain.PriceCatalogue.MasterProductSelectionPolicy</c>): it never chooses a status,
/// a match source or a search text, all of which are the server's.
/// <see cref="TrackSupplierPrices"/> matters only beside a selection, because the link <em>is</em>
/// the tracking row - <see langword="false"/> is the opt-out on the form and ignores the selection
/// entirely, leaving the product unlinked and untracked.</para>
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
    decimal? InitialUnitCost,
    long? SelectedMasterProductId = null,
    bool TrackSupplierPrices = true);
