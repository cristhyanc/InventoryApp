using Inventory.Application.PlatformReceiptScans;

namespace InventoryApi.DTOs;

/// <summary>
/// One business's receipt-scan usage as
/// <c>GET /api/super-admin/businesses/{businessId}/purchase-scan-usage</c> reports it (issue #1164,
/// sub-issue C of #975).
///
/// <para>These five fields are the whole contract, and what is missing from it is the point: no
/// receipt, no file name, no product, no supplier, no purchase, no instant and nothing about any
/// other business. A platform administrator asking "is this business near its scan cap?" needs a
/// count, a cap and the month they are about, and that is all this is.</para>
///
/// <para><strong>The published type and route are named <c>PurchaseScan*</c>, never
/// <c>ReceiptScan*</c></strong> - the rule issue #127 established and
/// <c>PublishedResponseSchemaContractTests</c> enforces: no schema or tag in the published document
/// may carry a Receipt name. The scanned document is of course a receipt, which is what the
/// documentation and the Super Admin's own card call it.</para>
///
/// <para><see cref="BusinessId"/> is reported for the same reason the directory response reports one
/// (AGENTS.md § Tenant ownership): here it is the subject of the read that the route named, it
/// resolves no business for any request, and it is never stamped on a row.</para>
/// </summary>
/// <param name="BusinessId">The business's own key, from the stored row the read found.</param>
/// <param name="Month">
/// The business's <strong>own</strong> calendar month the count is for, as <c>yyyy-MM</c>, derived in
/// the time zone stored on that business. It is reported rather than assumed because the platform
/// page is not in any business's zone, and a count shown without its month is a count against an
/// unknown cap.
/// </param>
/// <param name="Used">How many scans the business has made in that month.</param>
/// <param name="Remaining">How many are left in it. Never negative.</param>
/// <param name="Limit">The cap, which is the same for every business.</param>
public sealed record SuperAdminPurchaseScanUsageResponse(
    int BusinessId,
    string Month,
    int Used,
    int Remaining,
    int Limit)
{
    public static SuperAdminPurchaseScanUsageResponse From(BusinessReceiptScanUsageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new SuperAdminPurchaseScanUsageResponse(
            view.BusinessId,
            view.Usage.Month,
            view.Usage.Used,
            view.Usage.Remaining,
            view.Usage.Limit);
    }
}
