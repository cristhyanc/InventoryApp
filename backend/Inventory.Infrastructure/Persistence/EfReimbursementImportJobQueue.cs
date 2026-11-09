using Inventory.Application.Imports;
using Inventory.Application.Time;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IReimbursementImportJobQueue"/> (issue #1149, the
/// repository owner's approval of 9 November 2026): <strong>one read-only statement</strong> that
/// answers which business's reimbursement import job is due next.
///
/// <para><strong>Why it has to ignore the tenant query filters.</strong> It runs before any business
/// scope exists, because the business it is looking for is the answer rather than the input: a queued
/// job is the only thing that says whose work is waiting. The worker's own loop is not a request and
/// resolves no actor, so the context it holds has resolved no business and the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of the job rows -
/// the queue would be silently empty forever. This is therefore the <strong>ninth</strong> reviewed
/// <c>IgnoreQueryFilters</c> path and the <strong>second</strong> that is not in a request at all
/// (AGENTS.md § Tenant ownership and data isolation). The alternatives are the ones issue #544
/// already rejected and are worse here: an unscoped <c>AppDbContext</c> would hand one adapter every
/// tenant-owned table at once, and resolving each business into a scope in turn would mean
/// impersonating every business to find one job.</para>
///
/// <para><strong>What makes it narrow is what it may touch, and it is written out here.</strong> One
/// method, which takes no argument but a cancellation token - no business id, no predicate, no column
/// list, no page, no sort, no state to ask for. One statement, over one table, projecting two
/// columns of it: the job's own key and the ownership key, which is not business data. No file name,
/// no uploaded bytes, no uploader identity, no attempt count, no error text, no count of anything,
/// and nothing a business trades, buys, sells, counts, costs, reports or stores as a credential. It
/// cannot be written through, and everything the chosen job then does happens in a service scope
/// resolved to that one business, where the ordinary filters and <c>BusinessOwnershipEnforcer</c> are
/// fully in force.</para>
///
/// <para><strong>Why "due" rather than "queued".</strong> A job whose attempt failed is queued again
/// with a not-before instant, and taking the oldest <em>queued</em> job would let it hold up every
/// job behind it - of its own business and of every other - for the length of its back-off. The
/// predicate is therefore the clock's: a job is due when it is not final and its not-before instant
/// has passed or it has none. <c>Processing</c> is due too, which is what resumes a job a restart
/// interrupted; one worker runs at a time, so a job that is genuinely in progress is not being asked
/// about.</para>
/// </summary>
public sealed class EfReimbursementImportJobQueue : IReimbursementImportJobQueue
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfReimbursementImportJobQueue(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<DueReimbursementImportJob?> FindNextDueAsync(
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // The one statement, and the whole of it. Ordered by the upload instant and then by the key,
        // so a backlog is worked through in the order the uploads arrived and the order is the same
        // every time rather than whatever the query plan produced.
        var due = await _db.ReimbursementImportJobs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(job => job.State == ReimbursementImportJobState.Queued
                || job.State == ReimbursementImportJobState.Processing)
            .Where(job => job.NextAttemptAtUtc == null || job.NextAttemptAtUtc <= now)
            .OrderBy(job => job.UploadedAtUtc)
            .ThenBy(job => job.Id)
            .Select(job => new { job.Id, job.BusinessId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return due is null ? null : new DueReimbursementImportJob(due.Id, due.BusinessId);
    }
}
