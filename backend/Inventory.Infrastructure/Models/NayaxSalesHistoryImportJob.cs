using System.Text.Json.Serialization;
using Inventory.Domain.Imports;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One uploaded Nayax sales history and where its import has got to (issue #1304).
///
/// <para><strong>Tenant-owned, with nothing added.</strong> A business's sales history is a fact
/// about that business, so <c>AppDbContext.ConfigureBusinessOwnership</c> gives this row the business
/// key, its index, the central query filter and the ownership stamp. The worker reaches it in a
/// service scope resolved to that one business, exactly as a request would, and the one
/// cross-business statement in this feature projects a job key and a business key and nothing else
/// (see <c>Inventory.Application.Imports.INayaxSalesHistoryImportJobQueue</c>).</para>
///
/// <para><strong>It is a separate table from the reimbursement import's job, not a type column on
/// it.</strong> The two jobs share a shape and nothing else: a reimbursement job's unit of work is a
/// file inside an archive and it records one outcome row per file, whereas a history's is a batch of
/// rows and what it records is a running count and the row to resume from. One table carrying both
/// would mean every column of each being nullable for the other, and the reimbursement import -
/// which this issue puts explicitly out of scope - being migrated to make room for it.</para>
///
/// <para><strong>The uploaded bytes live here until the job finishes</strong>, which is where and how
/// the reimbursement import stores its archive. They have to: a job resumed after a restart reads
/// from the file it was uploaded with, and nothing else holds a copy - the file is never written to
/// disk. They are deleted the moment the job reaches a final state, because a file nobody will read
/// again is a business document this application has no reason to keep.</para>
///
/// <para><strong>The progress counts are stored, and that is deliberate.</strong> The reimbursement
/// job stores none and counts its files from their outcome rows; a history has no per-row rows to
/// count - 250,000 of them would be a table bigger than the sales they describe - so the counts are
/// columns, written in the same transaction as the sales they describe. <see cref="RowsRead"/> is the
/// one that is also an instruction: it is where the next attempt resumes from, so it may only ever
/// be the number of data rows whose batch actually committed.</para>
/// </summary>
public class NayaxSalesHistoryImportJob : IBusinessOwned
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
    /// <c>Inventory.Domain.Imports.NayaxSalesHistoryPolicy.SanitiseFileName</c>. It is a label,
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
    public required NayaxSalesHistoryImportJobState State { get; set; }

    /// <summary>
    /// How many attempts have been <em>started</em>. Counted at the start so a process that died
    /// mid-job has already spent one, which is what bounds a job that crashes its own host.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// How many data rows the file carries, counted by the upload's own streamed check. A fact about
    /// the file, reported beside the progress so an operator can see how far through it the job is.
    /// </summary>
    public int TotalRows { get; set; }

    /// <summary>
    /// How many data rows have been applied and <em>committed</em>, which is also where the next
    /// attempt resumes from.
    /// </summary>
    public int RowsRead { get; set; }

    /// <summary>Rows stored as a new sale.</summary>
    public int Created { get; set; }

    /// <summary>Rows that updated a sale this business already held.</summary>
    public int Updated { get; set; }

    /// <summary>Rows applied nowhere: no usable identity, instant, or - for a new sale - amount.</summary>
    public int Skipped { get; set; }

    /// <summary>Rows carrying a value that could not be read. Each changed nothing.</summary>
    public int Invalid { get; set; }

    /// <summary>
    /// Updated rows that kept a stored value because the file's cell was blank or its column was
    /// missing (issue #1298), counted once per row.
    /// </summary>
    public int KeptStoredValues { get; set; }

    /// <summary>
    /// The last safe reason recorded about the job itself, or <c>null</c>. Safe means what it says: a
    /// failure type and what to do about it, never a sale, a figure, a payment detail or a database
    /// message, which can quote the parameter values of the statement that failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>The uploaded CSV, held until the job reaches a final state and deleted then.</summary>
    public byte[]? File { get; set; }

    /// <summary>When the job reached a final state, or <c>null</c> while it has not.</summary>
    public DateTime? FinishedAtUtc { get; set; }
}
