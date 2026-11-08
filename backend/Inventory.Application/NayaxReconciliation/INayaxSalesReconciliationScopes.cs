namespace Inventory.Application.NayaxReconciliation;

/// <summary>
/// One service scope whose current business is a business the hourly reconciliation is running for
/// (issue #1104), and the only thing the runner may do inside it.
///
/// <para>Disposing it ends that scope, and with it the <c>AppDbContext</c> everything in it read and
/// wrote - exactly as the end of a request does. One business is one scope: the reconciliation of
/// business A can therefore never reuse the context, the credential read, the plan answer or the
/// change tracker of business B.</para>
/// </summary>
public interface INayaxSalesReconciliationBusinessScope : IAsyncDisposable
{
    /// <summary>
    /// Runs <see cref="ReconcileNayaxSalesWithProvider"/> for this business inside that scope.
    /// </summary>
    Task<NayaxSalesReconciliationResult> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The two service scopes the hourly Nayax sales reconciliation needs, behind one port so its loop
/// stays free of the hosting container (issue #1104). It is deliberately
/// <c>INayaxSqsReaderScopes</c>'s shape (issue #1103), for the same reasons.
///
/// <para><strong>They are two scopes and not one, and the difference is the whole tenancy
/// story.</strong> Deciding <em>which</em> businesses to run for happens in a scope that has resolved
/// <em>no</em> business - which is what makes the eligibility statement a reviewed
/// <c>IgnoreQueryFilters</c> read rather than something a tenant filter could answer, since the
/// businesses are the answer rather than the input - and everything a business's run then does
/// happens in a scope resolved to that one business, where the ordinary filters and
/// <c>BusinessOwnershipEnforcer</c> are fully in force. Neither scope outlives what it was opened
/// for.</para>
///
/// <para><strong>The business ids come from the eligibility read and from nowhere else.</strong>
/// There is no configuration, no argument and no message that chooses one: a business is reconciled
/// because its own sender row is on and its own lifecycle is active. That is the same shape as
/// <c>ConnectedMachineSnapshotCommand</c>'s per-business recorder, where the id comes from the
/// business row the enumeration read.</para>
/// </summary>
public interface INayaxSalesReconciliationScopes
{
    /// <summary>
    /// Opens a scope that has resolved no business, asks which businesses are eligible, and closes it
    /// again.
    /// </summary>
    Task<IReadOnlyList<int>> ListEligibleBusinessIdsAsync(CancellationToken cancellationToken);

    /// <summary>Opens a scope resolved to <paramref name="businessId"/>.</summary>
    INayaxSalesReconciliationBusinessScope CreateForBusiness(int businessId);
}
