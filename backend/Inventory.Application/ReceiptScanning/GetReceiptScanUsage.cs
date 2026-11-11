using Inventory.Application.Time;
using Inventory.Domain.ReceiptScanning;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// How many receipt scans the current business has used this month, and how many are left (issue
/// #1164, sub-issue C of #975), as <c>GET /api/purchases/scan/usage</c> reports it.
///
/// <para><strong>It reads and changes nothing else.</strong> One counter of the business the request
/// already resolved, through the ordinary tenant query filters; no receipt, no draft, no purchase and
/// no other business. Asking how many scans are left is not a scan, so nothing is counted by it and
/// no outbound call is made.</para>
///
/// <para><strong>The month is the business's own.</strong> It is the calendar month
/// <see cref="IBusinessCalendar"/> derives in the business's configured zone, so a business reading
/// this at 11pm on the last day of a month sees that month and sees the next one an hour later - the
/// same boundary the scan itself counts against, because both come from here through one policy
/// (AGENTS.md § Reporting dates and filters).</para>
/// </summary>
public sealed class GetReceiptScanUsage
{
    private readonly IReceiptScanUsageStore _usage;
    private readonly IBusinessCalendar _calendar;

    public GetReceiptScanUsage(IReceiptScanUsageStore usage, IBusinessCalendar calendar)
    {
        _usage = usage;
        _calendar = calendar;
    }

    /// <summary>
    /// The current business's usage of its current month, with the cap it is measured against.
    /// </summary>
    public async Task<ReceiptScanUsageView> Handle(CancellationToken cancellationToken)
    {
        var monthKey = ReceiptScanQuotaPolicy.MonthKeyFor(_calendar.Today);
        var used = await _usage.ReadUsedAsync(monthKey, cancellationToken).ConfigureAwait(false);

        return new ReceiptScanUsageView(monthKey, used, PlanCatalogue.MonthlyReceiptScanLimit);
    }
}
