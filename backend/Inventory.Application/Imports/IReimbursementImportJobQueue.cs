namespace Inventory.Application.Imports;

/// <summary>
/// One due job, as the worker's discovery read answers it (issue #1149): a job key and the business
/// it belongs to, and nothing else at all.
/// </summary>
/// <param name="JobId">The job's own key.</param>
/// <param name="BusinessId">
/// The business the job belongs to. It is the <em>answer</em> rather than the input - which is
/// precisely why the read that produced it cannot be filtered by a business.
/// </param>
public readonly record struct DueReimbursementImportJob(int JobId, int BusinessId);

/// <summary>
/// Which reimbursement import job the worker should do next, across every business (issue #1149).
///
/// <para><strong>This is the one cross-tenant read this feature makes</strong>, and it is the
/// repository owner's approval of 9 November 2026. The worker's loop is not a request: it resolves
/// no actor and has deliberately resolved no business, because "whose job is waiting?" is what the
/// read is for. The central tenant filter would therefore match no job row at all and the queue
/// would be silently empty forever - the same reason the Nayax SQS sender directory's two statements
/// have (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para><strong>What makes it narrow is what it may answer with, and it is the whole of the
/// port.</strong> One method, taking no argument but a cancellation token - no business id, no
/// predicate, no column list, no page, no sort, no state to ask for. It projects a job key and a
/// business key, which is the ownership key itself and not business data: no file name, no uploaded
/// bytes, no uploader, no attempt count, no error, no count of anything, and nothing a business
/// trades. It is read-only. Everything the job then does happens in a service scope resolved to that
/// one business, where the ordinary filters and <c>BusinessOwnershipEnforcer</c> are fully in
/// force.</para>
/// </summary>
public interface IReimbursementImportJobQueue
{
    /// <summary>
    /// The oldest job that is <em>due</em> - queued or interrupted, and past any not-before instant
    /// a failed attempt gave it - or <see langword="null"/> when nothing is waiting.
    ///
    /// <para>Oldest by upload instant, so a backlog is worked through in the order the uploads
    /// arrived. "Due" rather than "queued" is what keeps a job waiting out its back-off from holding
    /// up every other job, of its own business or of any other.</para>
    /// </summary>
    Task<DueReimbursementImportJob?> FindNextDueAsync(CancellationToken cancellationToken);
}
