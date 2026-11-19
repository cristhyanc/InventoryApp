using Inventory.Domain.Imports;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Imports;

/// <summary>An accepted sales history upload, as the job row records it (issue #1304).</summary>
/// <param name="FileName">The sanitised name of the uploaded CSV.</param>
/// <param name="UploadedBy">
/// The member who uploaded it, as the validated Entra <c>(tid, oid)</c> pair that identifies them -
/// the same identity the <c>BusinessMembership</c> row is keyed by. No display name and no email
/// address is stored or published.
/// </param>
/// <param name="UploadedAtUtc">When the upload was accepted.</param>
/// <param name="TotalRows">
/// How many data rows the upload's own streamed check counted. It is a fact about the file, reported
/// beside the progress so an operator can see how far through it the job is.
/// </param>
/// <param name="File">
/// The uploaded bytes, kept with the job until it reaches a final state and deleted then - the same
/// place and on the same terms as the reimbursement import's archive.
/// </param>
public sealed record NewNayaxSalesHistoryImportJob(
    string FileName,
    ActorIdentity UploadedBy,
    DateTime UploadedAtUtc,
    int TotalRows,
    byte[] File);

/// <summary>What an upload turned into (issue #1304).</summary>
public enum NayaxSalesHistoryImportJobCreateState
{
    /// <summary>The upload is stored as a queued job.</summary>
    Created,

    /// <summary>
    /// This business already has a history import that has not finished, so nothing was stored. One
    /// runs at a time: a second would contend for the same write lock and the same sales rows for no
    /// gain, and two jobs importing overlapping files would each report half the answer.
    /// </summary>
    AlreadyRunning,
}

/// <summary>The answer to an upload (issue #1304).</summary>
/// <param name="State">What happened.</param>
/// <param name="JobId">The stored job's key, when one was created.</param>
public sealed record NayaxSalesHistoryImportJobCreateResult(
    NayaxSalesHistoryImportJobCreateState State, int? JobId)
{
    /// <summary>An upload stored as a queued job.</summary>
    public static NayaxSalesHistoryImportJobCreateResult Created(int jobId) =>
        new(NayaxSalesHistoryImportJobCreateState.Created, jobId);

    /// <summary>An upload refused because one is already running. Nothing was stored.</summary>
    public static NayaxSalesHistoryImportJobCreateResult AlreadyRunning { get; } =
        new(NayaxSalesHistoryImportJobCreateState.AlreadyRunning, null);
}

/// <summary>
/// How far one sales history import has got, counted from the batches it has actually committed
/// (issue #1304).
///
/// <para><strong>Every count is written in the same transaction as the sales it describes</strong>,
/// so "what has this job imported?" is answerable from the row itself even after a restart, and
/// <see cref="RowsRead"/> is also where the next attempt resumes from. The five numbers are the ones
/// the uploaded import reports for a whole file, minus its kept-values count - which a history
/// reports too, because a re-uploaded overlapping file is exactly where it matters.</para>
/// </summary>
/// <param name="RowsRead">Data rows read and applied, which is where a resumed attempt starts.</param>
/// <param name="Created">Rows stored as a new sale.</param>
/// <param name="Updated">Rows that updated a sale the business already held.</param>
/// <param name="Skipped">Rows with no usable identity, instant, or - for a new sale - amount.</param>
/// <param name="Invalid">Rows carrying a value that could not be read. Each changed nothing.</param>
/// <param name="KeptStoredValues">
/// Updated rows that kept a stored value because the file's cell was blank or its column was
/// missing, counted once per row.
/// </param>
public readonly record struct NayaxSalesHistoryImportProgress(
    int RowsRead, int Created, int Updated, int Skipped, int Invalid, int KeptStoredValues)
{
    /// <summary>This progress with one batch's outcome added to it.</summary>
    /// <param name="rows">How many data rows the batch held.</param>
    /// <param name="applied">What the shared row rules made of them.</param>
    public NayaxSalesHistoryImportProgress Plus(int rows, NayaxSalesAppliedRows applied)
    {
        ArgumentNullException.ThrowIfNull(applied);

        return new NayaxSalesHistoryImportProgress(
            RowsRead + rows,
            Created + applied.Imported,
            Updated + applied.Updated,
            Skipped + applied.Skipped,
            Invalid + applied.InvalidRows.Count,
            KeptStoredValues + applied.KeptStoredValues);
    }
}

/// <summary>Why a job could not be claimed, or that it was (issue #1304).</summary>
public enum NayaxSalesHistoryImportJobClaimState
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
    /// records the stop rather than reading anything.
    /// </summary>
    AttemptsExhausted,

    /// <summary>
    /// The job is not finished and its uploaded bytes are gone, so there is nothing left to import
    /// from. The caller stops the job with a safe reason instead of guessing.
    /// </summary>
    FileMissing,
}

/// <summary>One claimed job and everything the worker needs to continue it (issue #1304).</summary>
/// <param name="JobId">The job's own key.</param>
/// <param name="FileName">The sanitised uploaded file name, for logging.</param>
/// <param name="Attempts">How many attempts have now been started, including this one.</param>
/// <param name="File">The stored bytes to stream.</param>
/// <param name="Progress">
/// What the job has committed so far. Its <see cref="NayaxSalesHistoryImportProgress.RowsRead"/> is
/// how many data rows this attempt passes over before it applies anything, which is what makes a job
/// interrupted by a restart resume instead of starting again.
/// </param>
public sealed record NayaxSalesHistoryImportJobClaim(
    int JobId,
    string FileName,
    int Attempts,
    byte[] File,
    NayaxSalesHistoryImportProgress Progress);

/// <summary>The answer to a claim attempt (issue #1304).</summary>
/// <param name="State">What happened.</param>
/// <param name="Claim">
/// The claimed job, when <paramref name="State"/> is
/// <see cref="NayaxSalesHistoryImportJobClaimState.Claimed"/>.
/// </param>
public sealed record NayaxSalesHistoryImportJobClaimResult(
    NayaxSalesHistoryImportJobClaimState State, NayaxSalesHistoryImportJobClaim? Claim)
{
    /// <summary>A claimed job.</summary>
    public static NayaxSalesHistoryImportJobClaimResult Claimed(
        NayaxSalesHistoryImportJobClaim claim) =>
        new(NayaxSalesHistoryImportJobClaimState.Claimed, claim);

    /// <summary>A job that was not claimed, for the stated reason.</summary>
    public static NayaxSalesHistoryImportJobClaimResult Refused(
        NayaxSalesHistoryImportJobClaimState state) => new(state, null);
}

/// <summary>One job as the status endpoint reports it (issue #1304).</summary>
/// <param name="Id">The job's own key.</param>
/// <param name="FileName">The sanitised name of the uploaded file.</param>
/// <param name="UploadedAtUtc">When the upload was accepted.</param>
/// <param name="State">Where the job has got to.</param>
/// <param name="TotalRows">How many data rows the file carries.</param>
/// <param name="RowsRead">How many of them have been applied and committed.</param>
/// <param name="Created">Rows stored as a new sale.</param>
/// <param name="Updated">Rows that updated a sale already held.</param>
/// <param name="Skipped">Rows applied nowhere.</param>
/// <param name="Invalid">Rows carrying a value that could not be read.</param>
/// <param name="KeptStoredValues">Updated rows that kept a stored value.</param>
/// <param name="Attempts">How many attempts have been started.</param>
/// <param name="LastError">The last safe reason recorded for the job, if any.</param>
/// <param name="FinishedAtUtc">When the job reached a final state, if it has.</param>
public sealed record NayaxSalesHistoryImportJobSummary(
    int Id,
    string FileName,
    DateTime UploadedAtUtc,
    NayaxSalesHistoryImportJobState State,
    int TotalRows,
    int RowsRead,
    int Created,
    int Updated,
    int Skipped,
    int Invalid,
    int KeptStoredValues,
    int Attempts,
    string? LastError,
    DateTime? FinishedAtUtc);

/// <summary>
/// The narrow persistence port one business's sales history import jobs are stored and read through
/// (issue #1304).
///
/// <para><strong>Every method is tenant-scoped by the central mechanism</strong>, never by a
/// business predicate of its own and never by a business a caller supplied: the request's resolved
/// business for the upload and the status read, and the one business the worker opened its scope for.
/// A job of another business is therefore not found at all, and a row this import writes is stamped
/// by <c>BusinessOwnershipEnforcer</c> from that same scope.</para>
///
/// <para><strong>The progress write deliberately has no transaction of its own.</strong>
/// <see cref="SaveProgressAsync"/> is called inside the batch's own write-locked transaction, beside
/// the sales that batch applied and the pending cost rebuilds it recorded, so there is no moment at
/// which rows are committed without the record of how far the job got - which is exactly what a
/// resumed attempt reads to decide what is left to do.</para>
/// </summary>
public interface INayaxSalesHistoryImportJobStore
{
    /// <summary>
    /// Stores an accepted upload as a queued job and answers its key, or refuses it because this
    /// business already has one running.
    ///
    /// <para>The check and the insert are one write-locked operation in the adapter: "is one already
    /// running?" is a decision about what is stored, so reading it outside the write that depends on
    /// it would let two uploads a second apart both start.</para>
    /// </summary>
    Task<NayaxSalesHistoryImportJobCreateResult> CreateAsync(
        NewNayaxSalesHistoryImportJob job, CancellationToken cancellationToken);

    /// <summary>The most recently uploaded jobs of this business, newest first, with their progress.</summary>
    /// <param name="limit">How many to answer with; the caller's own fixed page size.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<IReadOnlyList<NayaxSalesHistoryImportJobSummary>> ListRecentAsync(
        int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the next attempt on a job: counts the attempt, moves the job to
    /// <see cref="NayaxSalesHistoryImportJobState.Processing"/> and answers what is left to do - all
    /// as one write, so a counted attempt and a claimed job cannot come apart.
    /// </summary>
    /// <param name="jobId">The job discovered by <see cref="INayaxSalesHistoryImportJobQueue"/>.</param>
    /// <param name="maxAttempts">
    /// How many attempts the job is allowed in total, from
    /// <c>Inventory.Domain.Imports.NayaxSalesHistoryPolicy.MaxAttempts</c>. The rule stays the
    /// caller's; the adapter only enforces the number it is given, inside the write it makes.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<NayaxSalesHistoryImportJobClaimResult> ClaimAsync(
        int jobId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>
    /// Records how far the job has got. Called inside the batch's own transaction, beside the sales
    /// that batch applied, and never on its own.
    /// </summary>
    Task SaveProgressAsync(
        int jobId, NayaxSalesHistoryImportProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Finishes a job whose every row has been applied: stores
    /// <see cref="NayaxSalesHistoryImportJobState.Completed"/> and deletes the uploaded bytes.
    /// </summary>
    Task CompleteAsync(int jobId, DateTime finishedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a job for good with a safe reason and deletes the uploaded bytes. Every batch it had
    /// already committed stays committed and its progress counts stay exactly as they are, so
    /// re-uploading the same file imports the rest and duplicates nothing.
    /// </summary>
    Task FailAsync(
        int jobId, string reason, DateTime finishedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a job back to <see cref="NayaxSalesHistoryImportJobState.Queued"/> with the safe reason
    /// its attempt ended on. The stored bytes and the progress stay: the next attempt resumes from
    /// the rows already committed.
    /// </summary>
    Task RequeueAsync(int jobId, string reason, CancellationToken cancellationToken);
}
