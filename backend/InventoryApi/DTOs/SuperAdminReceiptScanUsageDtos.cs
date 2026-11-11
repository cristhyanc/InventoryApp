using Inventory.Application.PlatformReceiptScans;

namespace InventoryApi.DTOs;

/// <summary>
/// One business's receipt-scan usage as
/// <c>GET /api/super-admin/businesses/{businessId}/receipt-scan-usage</c> reports it (issue #1164,
/// sub-issue C of #975).
///
/// <para>These five fields are the whole contract, and what is missing from it is the point: no
/// receipt, no file name, no product, no supplier, no purchase, no instant and nothing about any
/// other business. A platform administrator asking "is this business near its scan cap?" needs a
/// count, a cap and the month they are about, and that is all this is.</para>
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
public sealed record SuperAdminReceiptScanUsageResponse(
    int BusinessId,
    string Month,
    int Used,
    int Remaining,
    int Limit)
{
    public static SuperAdminReceiptScanUsageResponse From(BusinessReceiptScanUsageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new SuperAdminReceiptScanUsageResponse(
            view.BusinessId,
            view.Usage.Month,
            view.Usage.Used,
            view.Usage.Remaining,
            view.Usage.Limit);
    }
}
