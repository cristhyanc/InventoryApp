namespace Inventory.Application.NayaxReconciliation;

/// <summary>
/// "When did this business's hourly reconciliation last <strong>fully</strong> succeed?" (issue
/// #1104) - the one column issue #1106 reads to decide whether the Dashboard may skip syncing at all.
///
/// <para><strong>It is written only by a run that did everything.</strong> The machine list and
/// <c>lastSales</c> for <em>every</em> machine were read without error, persistence committed, every
/// comparison and absence observation of the run was recorded, and no pending inventory-cost rebuild
/// this business has was left behind. A run that was skipped - the lock was held, the allowance was
/// spent, the gate refused it - or that failed anywhere leaves the value exactly as it was, because a
/// value that moved on a partial run would be a promise nothing kept. A successful Nayax read on its
/// own never sets it either.</para>
///
/// <para>It is the sender row's own column (issue #1101) and an ordinary tenant-owned update in the
/// business's own scope: no business id parameter, no <c>IgnoreQueryFilters</c> and no write lease -
/// the same shape as <c>INayaxSqsSenderActivityStore</c> beside it.</para>
/// </summary>
public interface INayaxReconciliationSuccessStore
{
    /// <summary>
    /// Records that this business's reconciliation run, which began at
    /// <paramref name="runStartedAtUtc"/>, succeeded in full.
    ///
    /// <para>The <em>start</em> instant is recorded rather than the finish, deliberately: what the
    /// value means is "everything up to here has been reconciled", and a run that took ten minutes
    /// reconciled the world as it was when it started reading.</para>
    /// </summary>
    Task RecordSucceededAsync(DateTime runStartedAtUtc, CancellationToken cancellationToken);
}
