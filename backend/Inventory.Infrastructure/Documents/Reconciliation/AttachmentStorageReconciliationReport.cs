using Inventory.Application.Documents;

namespace Inventory.Infrastructure.Documents.Reconciliation;

/// <summary>What the reconciliation found about one record or one stored file (issue #968).</summary>
public enum AttachmentStorageFindingKind
{
    /// <summary>A record refers to a document storage has no file for.</summary>
    MissingFile = 10,

    /// <summary>A record has a document whose size was never recorded, and storage could not be read either.</summary>
    UnknownSize = 20,

    /// <summary>A record's unknown size was filled in from the stored file (apply runs only).</summary>
    FilledSize = 30,

    /// <summary>A stored file no record refers to. It is never deleted, and never charged to a business.</summary>
    OrphanFile = 40,
}

/// <summary>
/// One finding, with enough to locate it and nothing about what the document contains: the kind of
/// attachment, the record's own id where there is a record, and the server-generated stored name.
/// No file name a person typed, no supplier, no amount, no business name.
/// </summary>
/// <param name="Kind">What was found.</param>
/// <param name="Category">Which kind of attachment it concerns.</param>
/// <param name="RecordId">
/// The purchase or operating expense, or <see langword="null"/> for an orphan file, which by
/// definition belongs to no record.
/// </param>
/// <param name="StoredFileName">The server-generated stored name.</param>
/// <param name="ByteLength">
/// The bytes involved: the stored file's length for an orphan or a filled size, and
/// <see langword="null"/> when nothing could be measured.
/// </param>
public sealed record AttachmentStorageFinding(
    AttachmentStorageFindingKind Kind,
    DocumentCategory Category,
    int? RecordId,
    string StoredFileName,
    long? ByteLength);

/// <summary>One business's reconciliation, as the command prints it (issue #968).</summary>
/// <param name="BusinessId">Whose attachments these are. An id, never a name.</param>
/// <param name="Attachments">How many records of this business refer to a document.</param>
/// <param name="ChargedBytes">
/// What those records' recorded sizes add up to - the stored half of what the quota charges this
/// business, as <c>GET /api/me/access</c> reports it. Reservations are deliberately not counted
/// here: an upload in progress is not something to reconcile against storage.
/// </param>
/// <param name="Findings">Everything that needs attention, in the order it was found.</param>
public sealed record AttachmentStorageBusinessReport(
    int BusinessId,
    int Attachments,
    long ChargedBytes,
    IReadOnlyList<AttachmentStorageFinding> Findings)
{
    public int MissingFiles => Count(AttachmentStorageFindingKind.MissingFile);

    public int UnknownSizes => Count(AttachmentStorageFindingKind.UnknownSize);

    public int FilledSizes => Count(AttachmentStorageFindingKind.FilledSize);

    public int OrphanFiles => Count(AttachmentStorageFindingKind.OrphanFile);

    /// <summary>
    /// What the orphan files of this business occupy. Reported because it is the one figure an
    /// operator needs to decide whether cleaning them up is worth a separate, reviewed operation -
    /// and it is never charged to the business.
    /// </summary>
    public long OrphanBytes => Findings
        .Where(finding => finding.Kind == AttachmentStorageFindingKind.OrphanFile)
        .Sum(finding => finding.ByteLength ?? 0);

    private int Count(AttachmentStorageFindingKind kind) =>
        Findings.Count(finding => finding.Kind == kind);
}

/// <summary>
/// The whole reconciliation (issue #968): one section per business, plus the stored files the
/// storage layout cannot attribute to any business at all.
///
/// <para><strong>It never deletes anything.</strong> An apply fills in sizes that were never
/// recorded and nothing else; orphan files and missing files are reported for a human to decide
/// about, because deleting a business's receipt is not recoverable by rolling a transaction
/// back.</para>
/// </summary>
/// <param name="DryRun">Whether this run wrote anything at all.</param>
/// <param name="Businesses">One section per business that has attachments or orphan files.</param>
/// <param name="UnattributedOrphans">
/// Stored files no record refers to, in a storage layout that does not say whose they are - which
/// is every orphan on the filesystem adapter. They are charged to nobody.
/// </param>
public sealed record AttachmentStorageReconciliationReport(
    bool DryRun,
    IReadOnlyList<AttachmentStorageBusinessReport> Businesses,
    IReadOnlyList<AttachmentStorageFinding> UnattributedOrphans)
{
    public int MissingFiles => Businesses.Sum(business => business.MissingFiles);

    public int UnknownSizes => Businesses.Sum(business => business.UnknownSizes);

    public int FilledSizes => Businesses.Sum(business => business.FilledSizes);

    public int OrphanFiles =>
        Businesses.Sum(business => business.OrphanFiles) + UnattributedOrphans.Count;

    /// <summary>
    /// <c>0</c> when nothing needs attention, <c>1</c> when something does - a record whose file is
    /// missing, a size that is still unknown, or a stored file nothing refers to. A dry run obeys
    /// the same rule, so an apply can be gated on a clean dry run, exactly as
    /// <c>migrate-documents</c> does. A size this run filled in is not "needing attention": it was
    /// resolved.
    /// </summary>
    public int ExitCode => MissingFiles + UnknownSizes + OrphanFiles > 0 ? 1 : 0;
}
