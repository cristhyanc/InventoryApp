using Inventory.Application.Reorder;

namespace Inventory.Application.Products;

/// <summary>
/// A product listing together with how the reorder facts behind it were obtained (issue #726).
///
/// <para>The reorder-alert listing is selected and ranked from a <see cref="ReorderNeedsResult"/>,
/// and that result's <see cref="ReorderNeedsResult.Mode"/> is part of what the listing <em>means</em>:
/// a row with <c>machineReplenishmentNeed: 0</c> in <see cref="ReorderMode.MachineAware"/> mode says
/// the machines need nothing, and the same row in <see cref="ReorderMode.StorageOnly"/> mode says the
/// machines were never read. Returning the products alone discarded that distinction and left a
/// caller of <c>GET /api/products/alerts/low-stock</c> or <c>GET /api/products?lowStockOnly=true</c>
/// no way to tell the two apart without fetching the dashboard summary as well.</para>
///
/// <para><see cref="Mode"/> is <see langword="null"/> for the plain catalogue listing, which makes no
/// reorder calculation at all: an absent mode is "no reorder facts were involved", never a guess at
/// which mode would have applied.</para>
/// </summary>
public sealed record ProductListing(
    IReadOnlyList<ProductRecord> Products,
    ReorderMode? Mode,
    MachineNeedUnavailableReason? MachineNeedUnavailableReason)
{
    /// <summary>
    /// The plain catalogue listing: no reorder needs were calculated, so there is no mode and no
    /// unavailability reason to publish.
    /// </summary>
    public static ProductListing Catalogue(IReadOnlyList<ProductRecord> products) => new(products, null, null);

    /// <summary>
    /// The reorder-alert listing, labelled with the mode the needs behind it were calculated in and -
    /// in storage-only mode - the reason the machines' need is missing. A factory rather than an
    /// object initializer so the two can only be copied from the result they describe.
    /// </summary>
    public static ProductListing ReorderAlerts(
        IReadOnlyList<ProductRecord> products, ReorderNeedsResult reorderNeeds) =>
        new(products, reorderNeeds.Mode, reorderNeeds.MachineNeedUnavailableReason);
}
