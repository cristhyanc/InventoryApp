using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSalesHistoryImportJobQueue"/> (issue #1304):
/// <strong>one read-only statement</strong> that answers which business's sales history import job
/// is waiting next.
///
/// <para><strong>Why it has to ignore the tenant query filters.</strong> It runs before any business
/// scope exists, because the business it is looking for is the answer rather than the input: an
/// unfinished job is the only thing that says whose work is waiting. The worker's own loop is not a
/// request and resolves no actor, so the context it holds has resolved no business and the central
/// filter <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of the job
/// rows - the queue would be silently empty forever. It is therefore a reviewed
/// <c>IgnoreQueryFilters</c> path of exactly the shape
/// <see cref="EfReimbursementImportJobQueue"/> is (issue #1149, AGENTS.md § Tenant ownership and data
/// isolation), added by this issue for the same reason and bounded in the same way. The alternatives
/// are the ones issue #544 already rejected and are worse here: an unscoped <c>AppDbContext</c> would
/// hand one adapter every tenant-owned table at once, and resolving each business into a scope in
/// turn would mean impersonating every business to find one job.</para>
///
/// <para><strong>What makes it narrow is what it may touch, and it is written out here.</strong> One
/// method, which takes no argument but a cancellation token - no business id, no predicate, no column
/// list, no page, no sort, no state to ask for. One statement, over one table, projecting two columns
/// of it: the job's own key and the ownership key, which is not business data. No file name, no
/// uploaded bytes, no uploader identity, no attempt count, no progress count, no error text, and
/// nothing a business trades, buys, sells, counts, costs, reports or stores as a credential. It
/// cannot be written through, and everything the chosen job then does happens in a service scope
/// resolved to that one business, where the ordinary filters and <c>BusinessOwnershipEnforcer</c> are
/// fully in force.</para>
///
/// <para><strong>Why there is no not-before instant in it.</strong> A failed attempt on a history
/// job is queued again with no back-off: the job resumes from the batches it committed, the worker's
/// own idle delay is what stops it spinning, and the attempt count is what bounds it. So "due" and
/// "not finished" are the same predicate here, which is one fewer column and one fewer way for a job
/// to be invisible. <c>Processing</c> is included, which is what resumes a job a restart interrupted;
/// one worker runs at a time, so a job that is genuinely in progress is not being asked about.</para>
/// </summary>
public sealed class EfNayaxSalesHistoryImportJobQueue : INayaxSalesHistoryImportJobQueue
{
    private readonly AppDbContext _db;

    public EfNayaxSalesHistoryImportJobQueue(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DueNayaxSalesHistoryImportJob?> FindNextDueAsync(
        CancellationToken cancellationToken)
    {
        // The one statement, and the whole of it. Ordered by the upload instant and then by the key,
        // so a backlog is worked through in the order the uploads arrived and the order is the same
        // every time rather than whatever the query plan produced.
        var due = await _db.NayaxSalesHistoryImportJobs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(job => job.State == NayaxSalesHistoryImportJobState.Queued
                || job.State == NayaxSalesHistoryImportJobState.Processing)
            .OrderBy(job => job.UploadedAtUtc)
            .ThenBy(job => job.Id)
            .Select(job => new { job.Id, job.BusinessId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return due is null ? null : new DueNayaxSalesHistoryImportJob(due.Id, due.BusinessId);
    }
}
