using System.Text.Json.Serialization;
using Inventory.Domain.Imports;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One uploaded Nayax reimbursement report archive and where its import has got to (issue #1149).
///
/// <para><strong>Tenant-owned, with nothing added.</strong> A settlement archive is a fact about the
/// business whose money it settles, so <c>AppDbContext.ConfigureBusinessOwnership</c> gives it the
/// business key, its index, the central query filter and the ownership stamp. The worker reaches it
/// in a service scope resolved to that one business, exactly as a request would, and the one
/// cross-business statement in this feature projects a job key and a business key and nothing else
/// (see <c>Inventory.Application.Imports.IReimbursementImportJobQueue</c>).</para>
///
/// <para><strong>The uploaded bytes live here until the job finishes.</strong> They have to: a job
/// resumed after a restart imports from the archive it was uploaded with, and nothing else holds a
/// copy - the archive is never written to disk. They are deleted the moment the job reaches a final
/// state, because an archive nobody will read again is a settlement document this application has no
/// reason to keep.</para>
///
/// <para><strong>No count is stored.</strong> What was imported, skipped and failed is counted from
/// the <see cref="ReimbursementImportJobFile"/> rows every time it is asked for. A total maintained
/// beside those rows is a total that can disagree with them after a restart, and "what actually
/// landed?" has to be answerable from the rows themselves.</para>
/// </summary>
public class ReimbursementImportJob : IBusinessOwned
{
    /// <summary>The job's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The owning business (issue #64). Stamped from the resolved business on insert and immutable
    /// afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The uploaded file's name, reduced to a leaf label by
    /// <c>Inventory.Domain.Imports.ReimbursementArchivePolicy.SanitiseFileName</c>. It is a label,
    /// never a path, and nothing uses it to reach the filesystem.
    /// </summary>
    public required string FileName { get; set; }

    /// <summary>
    /// The uploading member's Entra directory tenant id (<c>tid</c>) - the first half of the pair
    /// that identifies them, and the same identity <c>BusinessMembership</c> is keyed by. No display
    /// name and no email address is stored here, and neither half is published by any endpoint.
    /// </summary>
    public required string UploadedByDirectoryTenantId { get; set; }

    /// <summary>The uploading member's Entra object id (<c>oid</c>).</summary>
    public required string UploadedByObjectId { get; set; }

    /// <summary>When the upload was accepted, as a UTC instant.</summary>
    public DateTime UploadedAtUtc { get; set; }

    /// <summary>Where the job has got to.</summary>
    public required ReimbursementImportJobState State { get; set; }

    /// <summary>
    /// How many attempts have been <em>started</em>. Counted at the start so a process that died
    /// mid-job has already spent one, which is what bounds a job that crashes its own host.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// The earliest instant a job waiting to retry may be picked up again, or <c>null</c> when it is
    /// not waiting. The worker takes the oldest job that is <em>due</em>, so a job backing off never
    /// holds up another - of its own business or of any other.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// The last safe error recorded about the job itself, or <c>null</c>. Safe means what it says: a
    /// failure type and what to do about it, never XML content, a figure, a payment detail or a
    /// database message, which can quote the parameter values of the statement that failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// The uploaded archive, held until the job reaches a final state and deleted then.
    /// </summary>
    public byte[]? Archive { get; set; }

    /// <summary>When the job reached a final state, or <c>null</c> while it has not.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>What each XML file in the archive turned into.</summary>
    public virtual ICollection<ReimbursementImportJobFile> Files { get; set; } =
        new List<ReimbursementImportJobFile>();
}

/// <summary>
/// What one XML file inside an uploaded archive turned into (issue #1149).
///
/// <para><strong>It is written in the same transaction as the rows it is about.</strong> A file's
/// reimbursement rows, the settlement coverage they prove and this row commit together or not at
/// all, so there is no moment at which a file is imported without the record of it, or recorded
/// without having been imported. That is the whole reason it is a row rather than a log line: the
/// next attempt reads it to decide what is left to do, and a file whose outcome was lost would be
/// offered to the content-hash check and reported as a duplicate of itself.</para>
///
/// <para><strong><see cref="EntryIndex"/> is the identity, not the name.</strong> The walk over the
/// same archive bytes is deterministic, so the n-th XML file is the same file on every attempt -
/// whereas two inner archives may legitimately hold a report of the same name.</para>
/// </summary>
public class ReimbursementImportJobFile : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The owning business (issue #64), stamped centrally like the job's own. A relationship between
    /// tenant-owned rows never crosses a business.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>The job this file belongs to.</summary>
    public int ReimbursementImportJobId { get; set; }

    /// <summary>The job itself.</summary>
    public virtual ReimbursementImportJob Job { get; set; } = null!;

    /// <summary>
    /// The file's position among the archive's XML entries, counted from zero. Unique per job, which
    /// the database enforces: two outcomes for one file would make the counts wrong.
    /// </summary>
    public int EntryIndex { get; set; }

    /// <summary>
    /// The sanitised entry label inside the archive, for display. Never a path, and nothing uses it
    /// to reach the filesystem.
    /// </summary>
    public required string EntryPath { get; set; }

    /// <summary>Imported, skipped as an already-imported duplicate, or failed.</summary>
    public required ReimbursementImportFileOutcome Outcome { get; set; }

    /// <summary>
    /// The safe reason a failure carries, and <c>null</c> otherwise. No XML content, no figure and
    /// no payment detail.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>When this outcome was committed, as a UTC instant.</summary>
    public DateTime RecordedAtUtc { get; set; }
}
