using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Imports;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Imports;

/// <summary>What one uploaded archive turned into (issue #1149).</summary>
/// <param name="JobId">The stored job's key, when the archive was accepted.</param>
/// <param name="Rejection">
/// The readable reason the archive was refused, when it was. Exactly one of the two is set.
/// </param>
public sealed record ReimbursementReportUploadResult(int? JobId, string? Rejection)
{
    /// <summary>An accepted archive, stored as a queued job.</summary>
    public static ReimbursementReportUploadResult Accepted(int jobId) => new(jobId, null);

    /// <summary>A refused archive. Nothing was stored and nothing was imported.</summary>
    public static ReimbursementReportUploadResult Refused(string rejection) => new(null, rejection);

    /// <summary>Whether the archive was accepted.</summary>
    public bool IsAccepted => JobId.HasValue;
}

/// <summary>
/// Accepts the ZIP Nayax emails, exactly as it arrives, and queues it for import (issue #1149).
///
/// <para><strong>It imports nothing.</strong> What it does is check the archive against
/// <see cref="ReimbursementArchivePolicy"/>, and - only if every limit holds and there is at least
/// one reimbursement XML inside - store the upload as a queued job for the background worker. That
/// separation is the point of the feature: a 1.3 MB archive holding a year of reports is accepted in
/// the moment, and the importing happens afterwards where a database problem can be retried instead
/// of becoming a failed upload.</para>
///
/// <para><strong>Breaking any limit rejects the whole upload.</strong> There is no partial accept:
/// an archive that is too large, too deep, too numerous or too compressible is refused with a
/// readable sentence and nothing at all is stored - not the bytes, not a job, not a single
/// reimbursement row. A partially accepted settlement archive would leave the operator unable to say
/// which half of their money landed.</para>
///
/// <para><strong>The business is never chosen by the request.</strong> The job is written in the
/// caller's own resolved scope and stamped by the central ownership enforcer; nothing in the upload
/// names a business, and the uploader is recorded as the validated Entra <c>(tid, oid)</c> pair the
/// token carried.</para>
/// </summary>
public sealed class UploadReimbursementReportArchive
{
    private readonly IReimbursementArchiveReader _reader;
    private readonly IReimbursementImportJobStore _jobs;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public UploadReimbursementReportArchive(
        IReimbursementArchiveReader reader,
        IReimbursementImportJobStore jobs,
        IAuthenticatedActorAccessor actors,
        IClock clock)
    {
        _reader = reader;
        _jobs = jobs;
        _actors = actors;
        _clock = clock;
    }

    /// <summary>
    /// Checks and stores one uploaded archive.
    /// </summary>
    /// <param name="fileName">The name the client sent, which is sanitised before it is stored.</param>
    /// <param name="archive">The uploaded bytes.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no identified actor. An upload with nobody behind it is refused rather than
    /// recorded with an empty uploader.
    /// </exception>
    public async Task<ReimbursementReportUploadResult> Handle(
        string? fileName, byte[] archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (archive.Length == 0)
            return ReimbursementReportUploadResult.Refused(ReimbursementArchivePolicy.EmptyUploadMessage);

        if (archive.Length > ReimbursementArchivePolicy.MaxUploadBytes)
            return ReimbursementReportUploadResult.Refused(ReimbursementArchivePolicy.TooLargeMessage);

        var contents = _reader.Read(archive);
        if (!contents.IsAccepted)
            return ReimbursementReportUploadResult.Refused(contents.Rejection!);

        if (contents.XmlFiles.Count == 0)
            return ReimbursementReportUploadResult.Refused(ReimbursementArchivePolicy.NoXmlMessage);

        var actor = RequireActor();
        var name = ReimbursementArchivePolicy.SanitiseFileName(fileName);

        var jobId = await _jobs.CreateAsync(
            new NewReimbursementImportJob(
                name.Length > 0 ? name : "reimbursement-report.zip",
                actor,
                _clock.UtcNow,
                archive),
            cancellationToken);

        return ReimbursementReportUploadResult.Accepted(jobId);
    }

    private ActorIdentity RequireActor()
    {
        var result = _actors.GetCurrentActor();

        return result.Actor
            ?? throw new BusinessAccessDeniedException(
                result.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
    }
}
