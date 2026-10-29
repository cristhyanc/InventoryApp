using Inventory.Application.Products;
using Inventory.Application.Reorder;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Publishes a reorder-alert listing's <see cref="ReorderMode"/> - and, in storage-only mode, the
/// reason the machines were not read - as response metadata beside the array the product endpoints
/// already return (issue #726, review repair).
///
/// <para><b>Why headers.</b> <c>GET /api/products/alerts/low-stock</c> and
/// <c>GET /api/products?lowStockOnly=true</c> answer a JSON array of products, and that is an
/// established contract several callers bind to. The mode is metadata about the whole answer rather
/// than about any row, and an empty array still has one - "nothing alerts, machines included" and
/// "nothing alerts on storage stock, the machines were never read" are different answers. A per-row
/// field could not carry it at all, and wrapping the array in an envelope would be a breaking change
/// this issue does not permit. So the metadata is additive response headers: an existing caller
/// ignores them and reads exactly the body it read before, and a caller that wants the distinction no
/// longer has to fetch <c>GET /api/dashboard/summary</c> to interpret a reorder-alert list.</para>
///
/// <para>The values are the same published codes as the dashboard's own sections -
/// <see cref="ReorderModes"/> and <see cref="MachineNeedUnavailableReasons"/> - so a client branches
/// on one vocabulary, never a sentence to display. The header names are listed in
/// <see cref="All"/> and that list is what <c>Program</c> exposes through the CORS policy; a browser
/// client cannot read a response header that is not exposed, so adding a header here without adding
/// it there would publish metadata the Angular application could not see.</para>
/// </summary>
public static class ReorderListingMetadataHeaders
{
    /// <summary>
    /// Which facts the reorder alerts in the body were selected from: <c>MachineAware</c> or
    /// <c>StorageOnly</c>. Absent on the plain catalogue listing, which calculates no reorder needs.
    /// </summary>
    public const string Mode = "X-Reorder-Mode";

    /// <summary>
    /// Why the machines' replenishment need is missing from a <c>StorageOnly</c> listing:
    /// <c>NayaxNotConnected</c>, <c>PermissionNotGranted</c> or - since issue #967 -
    /// <c>PlanRequired</c>, for a business whose plan does not include the sales provider. Absent in
    /// <c>MachineAware</c> mode, where there is nothing unavailable to explain.
    /// </summary>
    public const string MachineNeedUnavailableReason = "X-Reorder-Machine-Need-Unavailable-Reason";

    /// <summary>
    /// Every header this projection may publish, for the CORS policy to expose to browser clients.
    /// One list, so the two cannot get out of step.
    /// </summary>
    public static readonly string[] All = [Mode, MachineNeedUnavailableReason];

    /// <summary>
    /// Writes the listing's metadata onto <paramref name="response"/>, and nothing at all for a plain
    /// catalogue listing - an absent mode means no reorder calculation was made, so there is no mode
    /// to guess at.
    /// </summary>
    public static void Publish(HttpResponse response, ProductListing listing)
    {
        if (listing.Mode is not { } mode)
        {
            return;
        }

        response.Headers[Mode] = ReorderModes.Code(mode);

        if (listing.MachineNeedUnavailableReason is { } reason)
        {
            response.Headers[MachineNeedUnavailableReason] = MachineNeedUnavailableReasons.Code(reason);
        }
    }
}
