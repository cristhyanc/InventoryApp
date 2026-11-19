using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSalesHistoryImportJobStore"/> (issue #1304).
///
/// <para><strong>Every statement here is an ordinary tenant-scoped one.</strong> There is no
/// business predicate, no <c>IgnoreQueryFilters</c> and no business id parameter anywhere in this
/// adapter: the upload and the status read run in the caller's own resolved scope, and the worker's
/// writes run in the scope it opened for one business. A job of another business therefore is not
/// found at all, and the row the upload inserts is stamped centrally. The one cross-business
/// statement this feature makes is in <see cref="EfNayaxSalesHistoryImportJobQueue"/>, on its own and
/// visibly.</para>
/// </summary>
public sealed class EfNayaxSalesHistoryImportJobStore : INayaxSalesHistoryImportJobStore
{
    private readonly AppDbContext _db;

    public EfNayaxSalesHistoryImportJobStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<NayaxSalesHistoryImportJobCreateResult> CreateAsync(
        NewNayaxSalesHistoryImportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // BEGIN IMMEDIATE, so "does this business already have one running?" and the insert that
        // depends on the answer are one operation. Read outside the write, two uploads a second
        // apart would both find nothing running and both start - and the second would then import
        // the same rows under a second job, reporting half the answer twice.
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var running = await _db.NayaxSalesHistoryImportJobs
            .AsNoTracking()
            .AnyAsync(
                row => row.State == NayaxSalesHistoryImportJobState.Queued
                    || row.State == NayaxSalesHistoryImportJobState.Processing,
                cancellationToken);

        if (running)
            return NayaxSalesHistoryImportJobCreateResult.AlreadyRunning;

        var row = new NayaxSalesHistoryImportJob
        {
            FileName = job.FileName,
            UploadedByDirectoryTenantId = job.UploadedBy.DirectoryTenantId,
            UploadedByObjectId = job.UploadedBy.ObjectId,
            UploadedAtUtc = job.UploadedAtUtc,
            State = NayaxSalesHistoryImportJobState.Queued,
            TotalRows = job.TotalRows,
            File = job.File,
        };

        // Ownership is not set here: the row is stamped centrally from the resolved business on save
        // (issue #64).
        _db.NayaxSalesHistoryImportJobs.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return NayaxSalesHistoryImportJobCreateResult.Created(row.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NayaxSalesHistoryImportJobSummary>> ListRecentAsync(
        int limit, CancellationToken cancellationToken) =>
        await _db.NayaxSalesHistoryImportJobs
            .AsNoTracking()
            .OrderByDescending(job => job.UploadedAtUtc)
            .ThenByDescending(job => job.Id)
            .Take(limit)
            .Select(job => new NayaxSalesHistoryImportJobSummary(
                job.Id,
                job.FileName,
                job.UploadedAtUtc,
                job.State,
                job.TotalRows,
                job.RowsRead,
                job.Created,
                job.Updated,
                job.Skipped,
                job.Invalid,
                job.KeptStoredValues,
                job.Attempts,
                job.LastError,
                job.FinishedAtUtc))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<NayaxSalesHistoryImportJobClaimResult> ClaimAsync(
        int jobId, int maxAttempts, CancellationToken cancellationToken)
    {
        // BEGIN IMMEDIATE, so the read that decides whether the job may be started and the write
        // that counts the attempt are one operation. There is one worker, so this is defence against
        // a second process rather than against this loop racing itself - but a counted attempt that
        // did not claim the job, or a claimed job whose attempt was not counted, is exactly how a
        // retry limit stops bounding anything.
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var row = await _db.NayaxSalesHistoryImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null || NayaxSalesHistoryImportJobStates.IsFinal(row.State))
            return NayaxSalesHistoryImportJobClaimResult.Refused(
                NayaxSalesHistoryImportJobClaimState.NotClaimable);

        if (row.Attempts >= maxAttempts)
        {
            // Nothing is written: the caller records the stop, with the reason the rule owns.
            return NayaxSalesHistoryImportJobClaimResult.Refused(
                NayaxSalesHistoryImportJobClaimState.AttemptsExhausted);
        }

        var file = row.File;
        if (file is null || file.Length == 0)
        {
            return NayaxSalesHistoryImportJobClaimResult.Refused(
                NayaxSalesHistoryImportJobClaimState.FileMissing);
        }

        row.Attempts++;
        row.State = NayaxSalesHistoryImportJobState.Processing;

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return NayaxSalesHistoryImportJobClaimResult.Claimed(new NayaxSalesHistoryImportJobClaim(
            row.Id,
            row.FileName,
            row.Attempts,
            file,
            new NayaxSalesHistoryImportProgress(
                row.RowsRead, row.Created, row.Updated, row.Skipped, row.Invalid, row.KeptStoredValues)));
    }

    /// <inheritdoc />
    public async Task SaveProgressAsync(
        int jobId, NayaxSalesHistoryImportProgress progress, CancellationToken cancellationToken)
    {
        // No transaction of its own: the caller opened one around this write and the batch's sales,
        // which is what makes them one change.
        var row = await _db.NayaxSalesHistoryImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return;

        row.RowsRead = progress.RowsRead;
        row.Created = progress.Created;
        row.Updated = progress.Updated;
        row.Skipped = progress.Skipped;
        row.Invalid = progress.Invalid;
        row.KeptStoredValues = progress.KeptStoredValues;

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        int jobId, DateTime finishedAtUtc, CancellationToken cancellationToken)
    {
        var row = await _db.NayaxSalesHistoryImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return;

        row.State = NayaxSalesHistoryImportJobState.Completed;
        row.FinishedAtUtc = finishedAtUtc;

        // The file is deleted the moment the job is final: nothing will read it again, and a
        // business document this application has no reason to keep is one it should not keep.
        row.File = null;

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailAsync(
        int jobId, string reason, DateTime finishedAtUtc, CancellationToken cancellationToken)
    {
        var row = await _db.NayaxSalesHistoryImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return;

        row.State = NayaxSalesHistoryImportJobState.Failed;
        row.LastError = reason;
        row.FinishedAtUtc = finishedAtUtc;
        row.File = null;

        // Every progress count stays exactly as it is, so what the job did import stays reported -
        // and re-uploading the same file imports the rest without duplicating any of it.
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RequeueAsync(int jobId, string reason, CancellationToken cancellationToken)
    {
        var row = await _db.NayaxSalesHistoryImportJobs
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        if (row is null) return;

        row.State = NayaxSalesHistoryImportJobState.Queued;
        row.LastError = reason;

        // The file and the progress stay: the next attempt resumes from the rows already committed.
        await _db.SaveChangesAsync(cancellationToken);
    }
}
