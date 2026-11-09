using Inventory.Domain.Imports;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Imports;

/// <summary>An accepted upload, as the job row records it (issue #1149).</summary>
/// <param name="FileName">The sanitised name of the uploaded archive.</param>
/// <param name="UploadedBy">
/// The member who uploaded it, as the validated Entra <c>(tid, oid)</c> pair that identifies them -
/// the same identity the <c>BusinessMembership</c> row is keyed by, and the same pair the platform
/// audit rows record. No display name and no email address is stored or published.
/// </param>
/// <param name="UploadedAtUtc">When the upload was accepted.</param>
/// <param name="Archive">
/// The uploaded bytes, kept with the job until it reaches a final state and deleted then.
/// </param>
public sealed record NewReimbursementImportJob(
    string FileName,
    ActorIdentity UploadedBy,
    DateTime UploadedAtUtc,
    byte[] Archive);

/// <summary>What one file of a job turned into, as the worker records it (issue #1149).</summary>
/// <param name="Index">The file's position in the archive walk - the job's per-file identity.</param>
/// <param name="Label">The sanitised entry label, for display.</param>
/// <param name="Outcome">Imported, skipped as a duplicate, or failed.</param>
/// <param name="Reason">
/// The safe reason a failure carries, and <see langword="null"/> otherwise. Never XML content, a
/// figure, a payment detail or a database message.
/// </param>
public sealed record ReimbursementImportFileResult(
    int Index,
    string Label,
    ReimbursementImportFileOutcome Outcome,
    string? Reason);

/// <summary>Why a job could not be claimed, or that it was (issue #1149).</summary>
public enum ReimbursementImportJobClaimState
{
    /// <summary>The job is this worker's to process.</summary>
    Claimed,

    /// <summary>
    /// There is nothing to claim: no such job for this business, or it has already reached a final
    /// state. A job that finished between being discovered and being claimed is this, not an error.
    /// </summary>
    NotClaimable,

    /// <summary>
    /// The job has used every attempt it is allowed, so starting another one is refused. The caller
    /// records the stop rather than processing anything.
    /// </summary>
    AttemptsExhausted,

    /// <summary>
    /// The job is not finished and its uploaded bytes are gone, so there is nothing left to import
    /// from. The caller stops the job with a safe reason instead of guessing.
    /// </summary>
    ArchiveMissing,
}

/// <summary>One claimed job and everything the worker needs to continue it (issue #1149).</summary>
/// <param name="JobId">The job's own key.</param>
/// <param name="FileName">The sanitised uploaded archive name, for logging.</param>
/// <param name="Attempts">How many attempts have now been started, including this one.</param>
/// <param name="Archive">The stored bytes to walk.</param>
/// <param name="CompletedFileIndexes">
/// The files that already have a recorded outcome. They keep it exactly as recorded and are not
/// processed again, which is what makes a job interrupted by a restart safe to resume.
/// </param>
public sealed record ReimbursementImportJobClaim(
    int JobId,
    string FileName,
    int Attempts,
    byte[] Archive,
    IReadOnlyList<int> CompletedFileIndexes);

/// <summary>The answer to a claim attempt (issue #1149).</summary>
/// <param name="State">What happened.</param>
/// <param name="Claim">The claimed job, when <paramref name="State"/> is
/// <see cref="ReimbursementImportJobClaimState.Claimed"/>.</param>
public sealed record ReimbursementImportJobClaimResult(
    ReimbursementImportJobClaimState State,
    ReimbursementImportJobClaim? Claim)
{
    /// <summary>A claimed job.</summary>
    public static ReimbursementImportJobClaimResult Claimed(ReimbursementImportJobClaim claim) =>
        new(ReimbursementImportJobClaimState.Claimed, claim);

    /// <summary>A job that was not claimed, for the stated reason.</summary>
    public static ReimbursementImportJobClaimResult Refused(ReimbursementImportJobClaimState state) =>
        new(state, null);
}

/// <summary>One job as the status endpoints report it (issue #1149).</summary>
/// <param name="Id">The job's own key.</param>
/// <param name="FileName">The sanitised name of the uploaded archive.</param>
/// <param name="UploadedAtUtc">When the upload was accepted.</param>
/// <param name="State">Where the job has got to.</param>
/// <param name="Attempts">How many attempts have been started.</param>
/// <param name="NextAttemptAtUtc">
/// When a job waiting to retry may be picked up again, and <see langword="null"/> otherwise.
/// </param>
/// <param name="LastError">The last safe error recorded for the job itself, if any.</param>
/// <param name="FinishedAtUtc">When the job reached a final state, if it has.</param>
/// <param name="ImportedFiles">
/// How many of its files were imported - counted from the outcome rows, never from a running total.
/// </param>
/// <param name="SkippedFiles">How many were already imported by this business.</param>
/// <param name="FailedFiles">How many could not be imported.</param>
public sealed record ReimbursementImportJobSummary(
    int Id,
    string FileName,
    DateTime UploadedAtUtc,
    ReimbursementImportJobState State,
    int Attempts,
    DateTime? NextAttemptAtUtc,
    string? LastError,
    DateTime? FinishedAtUtc,
    int ImportedFiles,
    int SkippedFiles,
    int FailedFiles);

/// <summary>One file's recorded outcome, as the job detail reports it (issue #1149).</summary>
/// <param name="File">The sanitised entry label inside the archive.</param>
/// <param name="Outcome">Imported, skipped or failed.</param>
/// <param name="Reason">The safe reason a failure carries.</param>
public sealed record ReimbursementImportJobFileSummary(
    string File,
    ReimbursementImportFileOutcome Outcome,
    string? Reason);

/// <summary>One job with every file outcome recorded for it (issue #1149).</summary>
/// <param name="Job">The job itself.</param>
/// <param name="Files">Its per-file outcomes, in the archive's own order.</param>
public sealed record ReimbursementImportJobDetail(
    ReimbursementImportJobSummary Job,
    IReadOnlyList<ReimbursementImportJobFileSummary> Files);

/// <summary>
/// The narrow persistence port one business's reimbursement import jobs are stored and read through
/// (issue #1149).
///
/// <para><strong>Every method is tenant-scoped by the central mechanism</strong>, never by a
/// business predicate of its own and never by a business a caller supplied: the request's resolved
/// business for the upload and the two status reads, and the one business the worker opened its
/// scope for. A job of another business is therefore not found at all - which is what makes the
/// status endpoint's <c>404</c> the truth rather than a check.</para>
///
/// <para><strong>The job-level writes and the per-file ones are deliberately separate.</strong> A
/// file's outcome is written by <see cref="RecordFileOutcomeAsync"/> inside the same transaction as
/// that file's reimbursement rows and coverage records (see
/// <see cref="IReimbursementImportFileTransactions"/>), so there is no moment at which a file is
/// committed without the record of what happened to it. The job's own state changes are their own
/// small writes before and after that.</para>
/// </summary>
public interface IReimbursementImportJobStore
{
    /// <summary>Stores an accepted upload as a queued job and answers its key.</summary>
    Task<int> CreateAsync(NewReimbursementImportJob job, CancellationToken cancellationToken);

    /// <summary>
    /// The most recently uploaded jobs of this business, newest first, with their counts.
    /// </summary>
    /// <param name="limit">How many to answer with; the caller's own fixed page size.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<IReadOnlyList<ReimbursementImportJobSummary>> ListRecentAsync(
        int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One job of this business with its file outcomes, or <see langword="null"/> when this business
    /// has no job with that key.
    /// </summary>
    Task<ReimbursementImportJobDetail?> FindAsync(int jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the next attempt on a job: counts the attempt, moves the job to
    /// <see cref="ReimbursementImportJobState.Processing"/> and answers what is left to do - all as
    /// one write, so a counted attempt and a claimed job cannot come apart.
    /// </summary>
    /// <param name="jobId">The job discovered by <see cref="IReimbursementImportJobQueue"/>.</param>
    /// <param name="maxAttempts">
    /// How many attempts the job is allowed in total, from
    /// <c>Inventory.Domain.Imports.ReimbursementImportRetryPolicy</c>. The rule stays the caller's;
    /// the adapter only enforces the number it is given, inside the write it makes.
    /// </param>
    /// <param name="nowUtc">The instant the attempt starts.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<ReimbursementImportJobClaimResult> ClaimAsync(
        int jobId, int maxAttempts, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Records what one file turned into. Called inside the file's own transaction, beside the rows
    /// that file produced, and never on its own.
    /// </summary>
    Task RecordFileOutcomeAsync(
        int jobId,
        ReimbursementImportFileResult outcome,
        DateTime recordedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finishes a job whose files all have an outcome: computes the counts from those outcome rows,
    /// stores the state <c>stateFor</c> chose for them, and deletes the uploaded bytes.
    /// </summary>
    /// <param name="jobId">The job being finished.</param>
    /// <param name="stateFor">
    /// <c>Inventory.Domain.Imports.ReimbursementImportJobCompletion.StateFor</c>, so what a finished
    /// job is called stays one deterministic rule rather than a second copy in an adapter.
    /// </param>
    /// <param name="finishedAtUtc">The instant the job reached its final state.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<ReimbursementImportFileCounts> CompleteAsync(
        int jobId,
        Func<ReimbursementImportFileCounts, ReimbursementImportJobState> stateFor,
        DateTime finishedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Puts a job back to <see cref="ReimbursementImportJobState.Queued"/> with a not-before instant
    /// and the safe reason its attempt ended on. Outcomes already recorded are untouched.
    /// </summary>
    Task RequeueAsync(
        int jobId,
        DateTime nextAttemptAtUtc,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops a job for good with a safe reason, deletes the uploaded bytes, and leaves every outcome
    /// it had already recorded exactly as it is - so re-uploading the same archive imports only what
    /// is missing.
    /// </summary>
    Task FailAsync(
        int jobId, string reason, DateTime finishedAtUtc, CancellationToken cancellationToken);
}
