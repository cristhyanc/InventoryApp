using Inventory.Application.ReceiptScanning;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.PlatformReceiptScans;

/// <summary>
/// One business's receipt-scan usage for its own current month, as the Super Admin's business detail
/// shows it (issue #1164, sub-issue C of #975).
///
/// <para><strong>It reads one counter and nothing else.</strong> No receipt, draft, purchase,
/// product, supplier, sale, cost, machine, site, document, credential or report value is reachable
/// through it, and it <strong>writes nothing at all</strong>: looking at what a business has used
/// cannot change it, so there is no lease, no transaction and no revision anywhere on this path.</para>
///
/// <para><strong>The cap is the catalogue's, and the month is the business's own.</strong> The count
/// and the month come from the store - which resolves the month in the business's configured time
/// zone - and the cap from <c>PlanCatalogue</c>, so what the platform page shows and what a scan is
/// actually refused by are the same two numbers from the same two places.</para>
///
/// <para><strong>Who may reach it is decided at the boundary, and not here.</strong> The
/// platform-admin policy and its marker authorize the endpoint (class 2), the authorization
/// middleware and <c>BusinessScopeMiddleware</c> each evaluate that policy, and the request carries a
/// denied <c>BusinessScope</c> throughout - so the business id travels in as the subject of the read
/// and resolves no tenancy for anything.</para>
/// </summary>
public sealed class GetBusinessReceiptScanUsage
{
    private readonly IPlatformReceiptScanUsageStore _store;

    public GetBusinessReceiptScanUsage(IPlatformReceiptScanUsageStore store)
    {
        _store = store;
    }

    /// <summary>
    /// That business's current month, what it has used in it and the cap, or <see langword="null"/>
    /// when no business row has that key - which the endpoint answers as a <c>404</c>.
    /// </summary>
    public async Task<BusinessReceiptScanUsageView?> Handle(
        int businessId, CancellationToken cancellationToken)
    {
        var usage = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);

        return usage is null
            ? null
            : new BusinessReceiptScanUsageView(
                usage.BusinessId,
                new ReceiptScanUsageView(
                    usage.MonthKey, usage.Used, PlanCatalogue.MonthlyReceiptScanLimit));
    }
}

/// <summary>
/// What the Super Admin's business detail is told about one business's receipt scanning (issue
/// #1164): which business, and that business's own usage of its own current month.
/// </summary>
/// <param name="BusinessId">The business, from the stored row the read found.</param>
/// <param name="Usage">Its month, its count and the cap.</param>
public sealed record BusinessReceiptScanUsageView(int BusinessId, ReceiptScanUsageView Usage);
