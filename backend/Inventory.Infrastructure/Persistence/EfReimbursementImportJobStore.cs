using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IReimbursementImportJobStore"/> (issue #1149).
///
/// <para><strong>Every statement here is an ordinary tenant-scoped one.</strong> There is no
/// business predicate, no <c>IgnoreQueryFilters</c> and no business id parameter anywhere in this
/// adapter: the upload and the two status reads run in the caller's own resolved scope, and the
/// worker's writes run in the scope it opened for one business. A job of another business therefore
/// is not found at all, which is what makes the status endpoint's <c>404</c> the truth rather than a
/// comparison somebody could get wrong. The one cross-business statement this feature makes is in
/// <see cref="EfReimbursementImportJobQueue"/>, on its own and visibly.</para>
///
/// <para><strong>The counts are always read from the outcome rows.</strong> No count is stored, so
/// what a job reports is what it recorded - including after a restart that interrupted it.</para>
/// </summary>
public sealed class EfReimbursementImportJobStore : IReimbursementImportJobStore
{
    private readonly AppDbContext _db;

    public EfReimbursementImportJobStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        NewReimbursementImportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var row = new ReimbursementImportJob
        {
            FileName = job.FileName,
            UploadedByDirectoryTenantId = job.UploadedBy.DirectoryTenantId,
            UploadedByObjectId = job.UploadedBy.ObjectId,
            UploadedAtUtc = job.UploadedAtUtc,
            State = ReimbursementImportJobState.Queued,
            Archive = job.Archive,
        };

        // Ownership is not set here: the row is stamped centrally from the resolved business on save
        // (issue #64).
        _db.ReimbursementImportJobs.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        return row.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReimbursementImportJobSummary>> ListRecentAsync(
        int limit, CancellationToken cancellationToken) =>
        // Ordered and limited before the projection, not after: the counts in it are subqueries
        // over the outcome rows, and ordering by a projected record's property is not something the
        // provider can translate.
        await Summaries(
                _db.ReimbursementImportJobs
                    .AsNoTracking()
                    .OrderByDescending(job => job.UploadedAtUtc)
                    .ThenByDescending(job => job.Id)
                    .Take(limit))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ReimbursementImportJobDetail?> FindAsync(
        int jobId, CancellationToken cancellationToken)
    {
        var summary = await Summaries(
                _db.ReimbursementImportJobs.AsNoTracking().Where(row => row.Id == jobId))
            .SingleOrDefaultAsync(cancellationToken);

        if (summary is null) return null;

        var files = await _db.ReimbursementImportJobFiles
            .AsNoTracking()
            .Where(row => row.ReimbursementImportJobId == jobId)
            .OrderBy(row => row.EntryIndex)
            .Select(row => new ReimbursementImportJobFileSummary(
                row.EntryPath, row.Outcome, row.Reason))
            .ToListAsync(cancellationToken);

        return new ReimbursementImportJobDetail(summary, files);
    }

    /// <inheritdoc />
    public async Task<ReimbursementImportJobClaimResult> ClaimAsync(
        int jobId, int maxAttempts, DateTime nowUtc, CancellationToken cancellationToken)
    {
        // BEGIN IMMEDIATE, so the read that decides whether the job may be started and the write
        // that counts the attempt are one operation. There is one worker, so this is defence
        // against a second process rather than against this loop racing itself - but a counted
        // attempt that did not claim the job, or a claimed job whose attempt was not counted, is
        // exactly how a retry limit stops bounding anything.
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var row = await _db.ReimbursementImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null || ReimbursementImportJobStates.IsFinal(row.State))
            return ReimbursementImportJobClaimResult.Refused(ReimbursementImportJobClaimState.NotClaimable);

        if (row.Attempts >= maxAttempts)
        {
            // Nothing is written: the caller records the stop, with the reason the rule owns.
            return ReimbursementImportJobClaimResult.Refused(
                ReimbursementImportJobClaimState.AttemptsExhausted);
        }

        var archive = row.Archive;
        if (archive is null || archive.Length == 0)
        {
            return ReimbursementImportJobClaimResult.Refused(
                ReimbursementImportJobClaimState.ArchiveMissing);
        }

        row.Attempts++;
        row.State = ReimbursementImportJobState.Processing;
        row.NextAttemptAtUtc = null;

        await _db.SaveChangesAsync(cancellationToken);

        var completed = await _db.ReimbursementImportJobFiles
            .AsNoTracking()
            .Where(file => file.ReimbursementImportJobId == jobId)
            .Select(file => file.EntryIndex)
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return ReimbursementImportJobClaimResult.Claimed(new ReimbursementImportJobClaim(
            row.Id, row.FileName, row.Attempts, archive, completed));
    }

    /// <inheritdoc />
    public async Task RecordFileOutcomeAsync(
        int jobId,
        ReimbursementImportFileResult outcome,
        DateTime recordedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // No transaction of its own: the caller opened one around this write and the file's
        // reimbursement rows, which is what makes them one change.
        _db.ReimbursementImportJobFiles.Add(new ReimbursementImportJobFile
        {
            ReimbursementImportJobId = jobId,
            EntryIndex = outcome.Index,
            EntryPath = outcome.Label,
            Outcome = outcome.Outcome,
            Reason = outcome.Reason,
            RecordedAtUtc = recordedAtUtc,
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReimbursementImportFileCounts> CompleteAsync(
        int jobId,
        Func<ReimbursementImportFileCounts, ReimbursementImportJobState> stateFor,
        DateTime finishedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateFor);

        var outcomes = await _db.ReimbursementImportJobFiles
            .AsNoTracking()
            .Where(file => file.ReimbursementImportJobId == jobId)
            .Select(file => file.Outcome)
            .ToListAsync(cancellationToken);

        var counts = ReimbursementImportJobCompletion.Count(outcomes);

        var row = await _db.ReimbursementImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return counts;

        row.State = stateFor(counts);
        row.NextAttemptAtUtc = null;
        row.FinishedAtUtc = finishedAtUtc;

        // The archive is deleted the moment the job is final: nothing will read it again, and a
        // settlement document this application has no reason to keep is one it should not keep.
        row.Archive = null;

        await _db.SaveChangesAsync(cancellationToken);

        return counts;
    }

    /// <inheritdoc />
    public async Task RequeueAsync(
        int jobId, DateTime nextAttemptAtUtc, string reason, CancellationToken cancellationToken)
    {
        var row = await _db.ReimbursementImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return;

        row.State = ReimbursementImportJobState.Queued;
        row.NextAttemptAtUtc = nextAttemptAtUtc;
        row.LastError = reason;

        // The archive stays: the next attempt imports from it.
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailAsync(
        int jobId, string reason, DateTime finishedAtUtc, CancellationToken cancellationToken)
    {
        var row = await _db.ReimbursementImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return;

        row.State = ReimbursementImportJobState.Failed;
        row.NextAttemptAtUtc = null;
        row.LastError = reason;
        row.FinishedAtUtc = finishedAtUtc;
        row.Archive = null;

        // Every outcome already recorded stays exactly as it is, so re-uploading the same archive
        // imports only what is missing.
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One job projected with its counts, taken from the outcome rows in the same statement.
    /// </summary>
    private static IQueryable<ReimbursementImportJobSummary> Summaries(
        IQueryable<ReimbursementImportJob> jobs) =>
        jobs.Select(job => new ReimbursementImportJobSummary(
            job.Id,
            job.FileName,
            job.UploadedAtUtc,
            job.State,
            job.Attempts,
            job.NextAttemptAtUtc,
            job.LastError,
            job.FinishedAtUtc,
            job.Files.Count(file => file.Outcome == ReimbursementImportFileOutcome.Imported),
            job.Files.Count(file => file.Outcome == ReimbursementImportFileOutcome.Skipped),
            job.Files.Count(file => file.Outcome == ReimbursementImportFileOutcome.Failed)));
}
