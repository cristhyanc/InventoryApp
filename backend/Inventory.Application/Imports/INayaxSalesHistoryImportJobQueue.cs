namespace Inventory.Application.Imports;

/// <summary>
/// One due job, as the worker's discovery read answers it (issue #1304): a job key and the business
/// it belongs to, and nothing else at all.
/// </summary>
/// <param name="JobId">The job's own key.</param>
/// <param name="BusinessId">
/// The business the job belongs to. It is the <em>answer</em> rather than the input - which is
/// precisely why the read that produced it cannot be filtered by a business.
/// </param>
public readonly record struct DueNayaxSalesHistoryImportJob(int JobId, int BusinessId);

/// <summary>
/// Which sales history import job the worker should do next, across every business (issue #1304).
///
/// <para><strong>This is the one cross-tenant read this feature makes</strong>, and it is
/// <see cref="IReimbursementImportJobQueue"/>'s rule for the other background import: the worker's
/// loop is not a request, it resolves no actor and has deliberately resolved no business, because
/// "whose job is waiting?" is what the read is for. The central tenant filter would therefore match
/// no job row at all and the queue would be silently empty forever (AGENTS.md § Tenant ownership and
/// data isolation). The alternatives are the ones issue #544 already rejected and are worse here: an
/// unscoped <c>AppDbContext</c> would hand one adapter every tenant-owned table at once, and
/// resolving each business into a scope in turn would mean impersonating every business to find one
/// job.</para>
///
/// <para><strong>What makes it narrow is what it may answer with, and it is the whole of the
/// port.</strong> One method, taking no argument but a cancellation token - no business id, no
/// predicate, no column list, no page, no sort, no state to ask for. It projects a job key and a
/// business key, which is the ownership key itself and not business data: no file name, no uploaded
/// bytes, no uploader, no attempt count, no progress count, no error text, and nothing a business
/// trades. It is read-only. Everything the job then does happens in a service scope resolved to that
/// one business, where the ordinary filters and <c>BusinessOwnershipEnforcer</c> are fully in
/// force.</para>
/// </summary>
public interface INayaxSalesHistoryImportJobQueue
{
    /// <summary>
    /// The oldest job that is not finished - queued, or interrupted while processing - or
    /// <see langword="null"/> when nothing is waiting.
    ///
    /// <para>Oldest by upload instant, so a backlog is worked through in the order the uploads
    /// arrived. <c>Processing</c> is offered too, which is what resumes a job a restart interrupted;
    /// one worker runs at a time, so a job that is genuinely in progress is not being asked
    /// about.</para>
    /// </summary>
    Task<DueNayaxSalesHistoryImportJob?> FindNextDueAsync(CancellationToken cancellationToken);
}
