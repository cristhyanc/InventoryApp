using Inventory.Domain.ReceiptScanning;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// How much of a business's monthly receipt-scan allowance is gone (issue #1164, sub-issue C of
/// #975), as the scan response, <c>GET /api/purchases/scan/usage</c> and the Super Admin's own card
/// report it.
///
/// <para>It carries the month the count is about, so a client never has to work out which month a
/// counter belongs to - the answer is the business's own calendar month, which is not the caller's
/// browser's and not UTC's.</para>
/// </summary>
/// <param name="MonthKey">
/// The business's calendar month, as <c>year * 100 + month</c> (see
/// <see cref="ReceiptScanQuotaPolicy.MonthKeyFor"/>).
/// </param>
/// <param name="Used">How many scans have been counted in that month.</param>
/// <param name="Limit">The cap, from <c>Inventory.Domain.Subscriptions.PlanCatalogue</c>.</param>
public sealed record ReceiptScanUsageView(int MonthKey, int Used, int Limit)
{
    /// <summary>How many scans are left in the month, never negative.</summary>
    public int Remaining => ReceiptScanQuotaPolicy.Remaining(Used, Limit);

    /// <summary>The month as <c>yyyy-MM</c>, which is how it is published.</summary>
    public string Month => ReceiptScanQuotaPolicy.MonthLabel(MonthKey);
}

/// <summary>
/// The Application's narrow port over the current business's monthly receipt-scan counter (issue
/// #1164).
///
/// <para>It takes no business identifier and no predicate: every statement it makes is scoped to the
/// business the request already resolved, through the ordinary tenant query filters and the central
/// ownership stamp (AGENTS.md § Tenant ownership and data isolation). There is no method that reads
/// or writes another business's counter, which is what makes "usage is per business" structural
/// rather than a filter somebody has to remember - and the Super Admin's own read of one business's
/// count is a deliberately separate platform port, never a widening of this one.</para>
///
/// <para><strong>Counting is one serialised operation.</strong> The implementation reads the month's
/// counter, compares it with the cap and writes the increment inside one write transaction with the
/// write lock already held, so two concurrent scans cannot both pass a check that only one of them
/// fits. That is the attachment-quota reservation's rule (issue #968) for a counter rather than for
/// bytes, and it is the whole of the issue's concurrency criterion.</para>
///
/// <para><strong>There is no release, deliberately.</strong> A scan is counted when the reader is
/// about to be called, and a failure <em>after</em> that call stays counted: the money was spent. The
/// counter is therefore a plain increment with nothing to undo, which is also why it needs no expiry
/// and no housekeeping.</para>
/// </summary>
public interface IReceiptScanUsageStore
{
    /// <summary>
    /// How many scans the current business has counted in <paramref name="monthKey"/>. A month with
    /// no row has made none, which is zero rather than an absence a caller has to interpret.
    /// </summary>
    Task<int> ReadUsedAsync(int monthKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts one scan against <paramref name="monthKey"/>, or refuses it.
    ///
    /// <para>Returns the new count when the scan fits, and <see langword="null"/> when the month's
    /// allowance is gone - in which case nothing was written and no scan was counted. The comparison
    /// and the increment happen inside one write-locked transaction, so of two concurrent scans with
    /// one scan left exactly one is counted.</para>
    /// </summary>
    /// <param name="monthKey">The business's current calendar month.</param>
    /// <param name="limit">The cap to compare against, from the catalogue.</param>
    /// <param name="cancellationToken">Cancels the read and the increment.</param>
    Task<int?> TryCountScanAsync(int monthKey, int limit, CancellationToken cancellationToken = default);
}
