using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Imports;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Imports;

/// <summary>What one uploaded sales history turned into (issue #1304).</summary>
/// <param name="JobId">The stored job's key, when the file was accepted.</param>
/// <param name="Rejection">
/// The readable reason the file was refused, when it was. Exactly one of the two is set.
/// </param>
public sealed record NayaxSalesHistoryUploadResult(int? JobId, string? Rejection)
{
    /// <summary>An accepted file, stored as a queued job.</summary>
    public static NayaxSalesHistoryUploadResult Accepted(int jobId) => new(jobId, null);

    /// <summary>A refused file. Nothing was stored and nothing was imported.</summary>
    public static NayaxSalesHistoryUploadResult Refused(string rejection) => new(null, rejection);

    /// <summary>Whether the file was accepted.</summary>
    public bool IsAccepted => JobId.HasValue;
}

/// <summary>
/// Accepts a long Nayax sales history as a CSV and queues it for import (issue #1304).
///
/// <para><strong>It imports nothing.</strong> What it does is check the upload against
/// <see cref="NayaxSalesHistoryPolicy"/> - the byte limit, the format, the file's shape and the row
/// ceiling, counted by a streamed read that allocates nothing per row - and, only if every limit
/// holds, store the upload as a queued job for the background worker. That separation is the point
/// of the feature: a year of history is accepted in the moment, and the importing happens afterwards
/// in batches that each hold the database's write lock for a second or two instead of one upload
/// holding it for minutes.</para>
///
/// <para><strong>Breaking any limit refuses the whole upload.</strong> There is no partial accept:
/// too many bytes, the wrong format, an unreadable shape, no rows or too many rows is a readable
/// sentence and nothing at all is stored - not the bytes, not a job, not a single sale. So is a
/// second upload while one is still importing: one history runs per business at a time, and the
/// refusal says to wait.</para>
///
/// <para><strong>The normal upload is untouched.</strong> <c>POST api/imports/nayax-sales</c> keeps
/// its own formats, its 5,000-row ceiling and its synchronous answer exactly as issue #1300
/// delivered them; this is a second endpoint for a different operation, and the two share their row
/// rules rather than their limits.</para>
///
/// <para><strong>The business is never chosen by the request.</strong> The job is written in the
/// caller's own resolved scope and stamped by the central ownership enforcer; nothing in the upload
/// names a business, and the uploader is recorded as the validated Entra <c>(tid, oid)</c> pair the
/// token carried.</para>
/// </summary>
public sealed class UploadNayaxSalesHistory
{
    private readonly INayaxSalesHistoryCsvReader _reader;
    private readonly INayaxSalesHistoryImportJobStore _jobs;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public UploadNayaxSalesHistory(
        INayaxSalesHistoryCsvReader reader,
        INayaxSalesHistoryImportJobStore jobs,
        IAuthenticatedActorAccessor actors,
        IClock clock)
    {
        _reader = reader;
        _jobs = jobs;
        _actors = actors;
        _clock = clock;
    }

    /// <summary>
    /// Checks and stores one uploaded history file.
    /// </summary>
    /// <param name="fileName">The name the client sent, which decides the format and is sanitised before it is stored.</param>
    /// <param name="file">The uploaded bytes.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no identified actor. An upload with nobody behind it is refused rather than
    /// recorded with an empty uploader.
    /// </exception>
    public async Task<NayaxSalesHistoryUploadResult> Handle(
        string? fileName, byte[] file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length == 0)
            return NayaxSalesHistoryUploadResult.Refused(NayaxSalesHistoryPolicy.EmptyUploadMessage);

        if (file.Length > NayaxSalesHistoryPolicy.MaxUploadBytes)
            return NayaxSalesHistoryUploadResult.Refused(NayaxSalesHistoryPolicy.TooLargeMessage);

        // The format is decided by the uploaded name, as the normal import's is. A spreadsheet is
        // refused here rather than read: expanding one is the cost this import exists to avoid.
        if (!NayaxSalesHistoryPolicy.IsCsv(fileName))
            return NayaxSalesHistoryUploadResult.Refused(
                NayaxSalesHistoryPolicy.UnsupportedFormatMessage);

        var check = Check(file);
        if (check.State != NayaxSalesHistoryFileState.Readable)
            return NayaxSalesHistoryUploadResult.Refused(RejectionFor(check.State));

        var actor = RequireActor();
        var name = NayaxSalesHistoryPolicy.SanitiseFileName(fileName);

        var created = await _jobs.CreateAsync(
            new NewNayaxSalesHistoryImportJob(
                name.Length > 0 ? name : "nayax-sales-history.csv",
                actor,
                _clock.UtcNow,
                check.DataRows,
                file),
            cancellationToken);

        return created.State == NayaxSalesHistoryImportJobCreateState.Created
            ? NayaxSalesHistoryUploadResult.Accepted(created.JobId!.Value)
            : NayaxSalesHistoryUploadResult.Refused(NayaxSalesHistoryPolicy.AlreadyRunningMessage);
    }

    /// <summary>
    /// The file's shape and row count, from one streamed read over the uploaded bytes. The stream is
    /// this use case's own, so the reader is handed something positioned at the start and nothing
    /// else in the request can have moved it.
    /// </summary>
    private NayaxSalesHistoryFileCheck Check(byte[] file)
    {
        using var content = new MemoryStream(file, writable: false);

        return _reader.Check(content, NayaxSalesHistoryPolicy.MaxRows);
    }

    /// <summary>The sentence each refusal is reported with. Every one of them stores nothing.</summary>
    private static string RejectionFor(NayaxSalesHistoryFileState state) => state switch
    {
        NayaxSalesHistoryFileState.TooManyRows => NayaxSalesHistoryPolicy.TooManyRowsMessage,
        NayaxSalesHistoryFileState.NoRows => NayaxSalesHistoryPolicy.NoRowsMessage,
        _ => NayaxSalesHistoryPolicy.UnreadableMessage,
    };

    private ActorIdentity RequireActor()
    {
        var result = _actors.GetCurrentActor();

        return result.Actor
            ?? throw new BusinessAccessDeniedException(
                result.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
    }
}

/// <summary>
/// The caller's own business's recent sales history import jobs (issue #1304).
///
/// <para><strong>The page size is the server's.</strong> The request carries nothing - no page, no
/// filter, no business, no state to ask for - so there is no input to widen: the ten most recently
/// uploaded jobs are what this answers with, which is what the Imports page shows.</para>
/// </summary>
public sealed class ListNayaxSalesHistoryImportJobs
{
    /// <summary>How many jobs the list answers with: the latest ten.</summary>
    public const int PageSize = 10;

    private readonly INayaxSalesHistoryImportJobStore _jobs;

    public ListNayaxSalesHistoryImportJobs(INayaxSalesHistoryImportJobStore jobs)
    {
        _jobs = jobs;
    }

    /// <summary>The latest jobs of the caller's own business, newest first.</summary>
    public Task<IReadOnlyList<NayaxSalesHistoryImportJobSummary>> Handle(
        CancellationToken cancellationToken) =>
        _jobs.ListRecentAsync(PageSize, cancellationToken);
}
