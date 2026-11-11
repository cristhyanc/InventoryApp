using Inventory.Application.Time;
using Inventory.Domain.Imports;

namespace Inventory.Application.Imports;

/// <summary>What one attempt on one job did (issue #1149).</summary>
public enum ReimbursementImportJobRunOutcome
{
    /// <summary>Every file has an outcome and the job is finished, failures included.</summary>
    Finished,

    /// <summary>
    /// The attempt ended on a database failure and the job is queued again with a not-before
    /// instant. Everything it had already committed stays committed.
    /// </summary>
    Requeued,

    /// <summary>
    /// The job stopped for good - its attempts ran out, or its stored archive was gone. Whatever it
    /// had imported stays imported, with its outcomes.
    /// </summary>
    Stopped,

    /// <summary>There was nothing to do: the job had already reached a final state.</summary>
    NothingToDo,
}

/// <summary>What one attempt did, with the counts a log line reports (issue #1149).</summary>
/// <param name="Outcome">What happened to the job.</param>
/// <param name="Counts">
/// The files that have an outcome, counted from the outcome rows. Zero everywhere for an attempt
/// that could not start.
/// </param>
/// <param name="FilesProcessedNow">
/// How many files this attempt itself put through the pipeline, which is fewer than
/// <see cref="ReimbursementImportFileCounts.Total"/> for a resumed job.
/// </param>
public sealed record ReimbursementImportJobRunResult(
    ReimbursementImportJobRunOutcome Outcome,
    ReimbursementImportFileCounts Counts,
    int FilesProcessedNow);

/// <summary>
/// Imports one uploaded reimbursement report archive (issue #1149), in a service scope already
/// resolved to the business the job belongs to.
///
/// <para><strong>Each XML is its own unit of work</strong>, exactly as the pending-folder import's
/// files are: the file's reimbursement rows, the settlement coverage it proves and the job's record
/// of what happened to it are written in <em>one</em> transaction
/// (<see cref="IReimbursementImportFileTransactions"/>). There is no moment at which a file is
/// committed without its outcome, or an outcome exists for rows that were not committed - which is
/// what makes the whole job resumable without ever importing anything twice.</para>
///
/// <para><strong>A resumed job trusts what is recorded.</strong> A file that already has an outcome
/// keeps it exactly as it is and is not processed again; only files with no outcome go through the
/// pipeline. So a file this job imported before a restart still reports <c>Imported</c>. A file with
/// <em>no</em> outcome whose hash turns out to be present is reported <c>Skipped</c>, and that is
/// correct rather than a guess: had this job imported it, the same transaction would have written
/// its outcome.</para>
///
/// <para><strong>A bad file is never a retry.</strong> An entry that cannot be read as a
/// reimbursement report is that file's own <c>Failed</c> outcome, written the same way an import or
/// a duplicate is, and the job carries on to the next file. What is retried is a database failure,
/// which is transient for a single-writer SQLite file and is exactly what another attempt
/// fixes.</para>
///
/// <para><strong>Nothing here decides which job to do, or when.</strong> That is
/// <see cref="ReimbursementImportJobRunner"/>'s, over
/// <see cref="IReimbursementImportJobQueue"/>'s one cross-business read.</para>
/// </summary>
public sealed class ProcessReimbursementImportJob
{
    /// <summary>
    /// The safe reason a file that could not be read as a reimbursement report records. It names no
    /// XML content, no figure and no payment detail - a settlement file's contents do not belong in
    /// a status column any more than in a log line.
    /// </summary>
    public const string UnreadableFileReason =
        "The file could not be read as a Nayax reimbursement report.";

    /// <summary>The safe reason a job whose stored archive is no longer there records.</summary>
    public const string ArchiveMissingReason =
        "The uploaded ZIP is no longer available. Upload it again.";

    /// <summary>The safe reason a job whose stored archive could not be walked records.</summary>
    public const string ArchiveUnreadableReason =
        "The uploaded ZIP could not be read. Upload it again.";

    private readonly IReimbursementImportJobStore _jobs;
    private readonly IReimbursementArchiveReader _reader;
    private readonly ImportReimbursementXmlFile _importFile;
    private readonly IReimbursementImportFileTransactions _transactions;
    private readonly IClock _clock;

    public ProcessReimbursementImportJob(
        IReimbursementImportJobStore jobs,
        IReimbursementArchiveReader reader,
        ImportReimbursementXmlFile importFile,
        IReimbursementImportFileTransactions transactions,
        IClock clock)
    {
        _jobs = jobs;
        _reader = reader;
        _importFile = importFile;
        _transactions = transactions;
        _clock = clock;
    }

    /// <summary>
    /// Claims the job and works through whatever is left of it.
    /// </summary>
    public async Task<ReimbursementImportJobRunResult> Handle(
        int jobId, CancellationToken cancellationToken)
    {
        var claimed = await _jobs.ClaimAsync(
            jobId, ReimbursementImportRetryPolicy.MaxAttempts, _clock.UtcNow, cancellationToken);

        switch (claimed.State)
        {
            case ReimbursementImportJobClaimState.AttemptsExhausted:
                await _jobs.FailAsync(
                    jobId,
                    ReimbursementImportRetryPolicy.AttemptsExhaustedReason,
                    _clock.UtcNow,
                    cancellationToken);
                return Stopped();

            case ReimbursementImportJobClaimState.ArchiveMissing:
                await _jobs.FailAsync(jobId, ArchiveMissingReason, _clock.UtcNow, cancellationToken);
                return Stopped();

            case ReimbursementImportJobClaimState.Claimed:
                break;

            default:
                return new ReimbursementImportJobRunResult(
                    ReimbursementImportJobRunOutcome.NothingToDo, default, 0);
        }

        var claim = claimed.Claim!;
        var contents = _reader.Read(claim.Archive);

        if (!contents.IsAccepted)
        {
            // The archive passed every check at upload, so this is unreachable short of the stored
            // bytes being damaged. It is the job's own stop rather than an attempt: reading the same
            // bytes again would fail in exactly the same way, so retrying would only delay telling
            // the operator to upload it again.
            await _jobs.FailAsync(jobId, ArchiveUnreadableReason, _clock.UtcNow, cancellationToken);
            return Stopped();
        }

        var processed = 0;
        var recorded = claim.CompletedFileIndexes.ToHashSet();

        foreach (var entry in contents.XmlFiles)
        {
            if (recorded.Contains(entry.Index)) continue;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ImportOneFileAsync(jobId, entry, cancellationToken);
                processed++;
            }
            catch (Exception exception) when (NotCancellation(exception, cancellationToken))
            {
                // Everything a file itself can be wrong about has already become that file's own
                // outcome inside the transaction above, so what reaches here is the database: the
                // writer was busy, the connection dropped, a constraint the application does not
                // model refused the write. The transaction rolled back, so this file has neither
                // rows nor an outcome, and the attempt is counted rather than the file blamed.
                return await RequeueAsync(claim, exception, cancellationToken);
            }
        }

        var counts = await _jobs.CompleteAsync(
            jobId, ReimbursementImportJobCompletion.StateFor, _clock.UtcNow, cancellationToken);

        return new ReimbursementImportJobRunResult(
            ReimbursementImportJobRunOutcome.Finished, counts, processed);
    }

    /// <summary>
    /// One file, in one transaction: parse result, hash dedupe, the rows, the coverage, the outcome.
    /// </summary>
    private async Task ImportOneFileAsync(
        int jobId, ReimbursementArchiveXmlEntry entry, CancellationToken cancellationToken)
    {
        await using var transaction = await _transactions.BeginAsync(cancellationToken);

        var result = entry.Parsed is null
            ? new ReimbursementImportFileResult(
                entry.Index, entry.Label, ReimbursementImportFileOutcome.Failed, UnreadableFileReason)
            : await ImportParsedFileAsync(entry, cancellationToken);

        await _jobs.RecordFileOutcomeAsync(jobId, result, _clock.UtcNow, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<ReimbursementImportFileResult> ImportParsedFileAsync(
        ReimbursementArchiveXmlEntry entry, CancellationToken cancellationToken)
    {
        var outcome = await _importFile.Handle(entry.Parsed!, cancellationToken);

        return new ReimbursementImportFileResult(
            entry.Index,
            entry.Label,
            outcome.WasImported
                ? ReimbursementImportFileOutcome.Imported
                : ReimbursementImportFileOutcome.Skipped,
            null);
    }

    private async Task<ReimbursementImportJobRunResult> RequeueAsync(
        ReimbursementImportJobClaim claim, Exception exception, CancellationToken cancellationToken)
    {
        var nextAttempt = ReimbursementImportRetryPolicy.NextAttemptAtUtc(
            claim.Attempts, _clock.UtcNow);

        if (nextAttempt is null)
        {
            await _jobs.FailAsync(
                claim.JobId,
                ReimbursementImportRetryPolicy.AttemptsExhaustedReason,
                _clock.UtcNow,
                cancellationToken);

            return Stopped();
        }

        // The type name, never the message: a database message can quote a parameter value, and the
        // values in this transaction are settlement figures.
        await _jobs.RequeueAsync(
            claim.JobId, nextAttempt.Value, SafeReason(exception), cancellationToken);

        return new ReimbursementImportJobRunResult(
            ReimbursementImportJobRunOutcome.Requeued, default, 0);
    }

    private static string SafeReason(Exception exception) =>
        $"The database could not be written ({exception.GetType().Name}). It will be retried.";

    private static ReimbursementImportJobRunResult Stopped() =>
        new(ReimbursementImportJobRunOutcome.Stopped, default, 0);

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
