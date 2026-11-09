namespace Inventory.Application.Imports;

/// <summary>
/// The database transaction one file of a reimbursement import job runs in (issue #1149). Disposing
/// it without <see cref="CommitAsync"/> rolls back everything that file wrote - its reimbursement
/// rows, its coverage records <em>and</em> its job outcome row - which is what makes them one change.
/// </summary>
public interface IReimbursementImportFileTransaction : IAsyncDisposable
{
    /// <summary>Commits the file.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Begins the transaction one file of a reimbursement import job runs in (issue #1149), so the
/// file's rows and the record of what happened to it land together or not at all.
///
/// <para>It is its own narrow port, named for the one operation that needs it, rather than a method
/// on either store - exactly as <c>INayaxConnectionSaveTransactions</c> (issue #537) is. The
/// transaction spans <em>both</em> of them: <see cref="IImportedReimbursementStore"/>'s write and
/// <c>IProviderDataCoverageStore</c>'s coverage rows on one side, and
/// <see cref="IReimbursementImportJobStore.RecordFileOutcomeAsync"/> on the other. Putting it on one
/// would imply the other's writes were outside it. Which unit of work the adapters share is an
/// Infrastructure fact; what the Application layer states is that these writes are one operation.</para>
///
/// <para><strong>Why it matters is the half-done states it rules out.</strong> A file committed
/// without its outcome would be imported again by the next attempt, which the content hash would
/// then report as a duplicate - so a file this job genuinely imported would end up recorded as
/// skipped, and a reconciliation looking at the job would be told the wrong thing about settled
/// money. An outcome committed without its rows is worse: the job would claim a settlement period
/// was imported when nothing was.</para>
/// </summary>
public interface IReimbursementImportFileTransactions
{
    /// <summary>Begins the transaction one file's writes run in.</summary>
    Task<IReimbursementImportFileTransaction> BeginAsync(CancellationToken cancellationToken);
}
