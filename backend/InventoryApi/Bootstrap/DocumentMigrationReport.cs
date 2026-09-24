using Inventory.Application.Documents;
using Inventory.Infrastructure.Documents;

namespace InventoryApi.Bootstrap;

/// <summary>What the migration did, or would do, with one document record.</summary>
public enum DocumentMigrationStatus
{
    /// <summary>
    /// Dry run only: the destination is free and the source is readable, so an apply would copy
    /// it. Nothing was written. This is the expected state before an apply, not a problem.
    /// </summary>
    Pending = 0,

    /// <summary>Copied to the destination and verified there by size and SHA-256.</summary>
    Migrated = 1,

    /// <summary>
    /// The destination already holds these exact bytes, so there was nothing to do. This is what
    /// makes a rerun safe: the second run of a completed migration reports only this.
    /// </summary>
    AlreadyPresent = 2,

    /// <summary>
    /// Another record already accounts for this destination. The bytes are migrated once; this
    /// record is listed so the duplicate metadata is visible rather than silently collapsed.
    /// </summary>
    DuplicateReference = 3,

    /// <summary>
    /// The record names a stored document that is in neither the protected nor the legacy
    /// location. Nothing is written - an empty blob would be worse than an absent one - and the
    /// database is left alone.
    /// </summary>
    MissingSource = 4,

    /// <summary>
    /// Something else is already stored at this document's destination. It is never overwritten,
    /// renamed or deleted, and no metadata is changed: a human decides what those bytes are.
    /// </summary>
    Collision = 5,

    /// <summary>
    /// The record carries no usable business id, so there is no tenant prefix it could safely be
    /// written under. It is never migrated to a fallback business - that would hand one
    /// business's document to another.
    /// </summary>
    InvalidBusiness = 6,

    /// <summary>
    /// The copy or its verification did not complete. The source is untouched and the record is
    /// not counted as migrated.
    /// </summary>
    Failed = 7,
}

/// <summary>
/// One document record's outcome, carrying enough to find the record again and nothing that
/// would leak what the document contains. Hashes appear only as short prefixes, for correlating
/// a source and a destination in the report.
/// </summary>
/// <param name="RecordType">The entity the document hangs off, e.g. <c>Purchase</c>.</param>
/// <param name="RecordId">That entity's key.</param>
/// <param name="BusinessId">The owning business, as persisted on the record.</param>
/// <param name="Category">Which kind of document it is.</param>
/// <param name="StoredFileName">The server-generated name held on the record.</param>
/// <param name="Status">What happened.</param>
/// <param name="Source">Where the bytes were found, when they were found at all.</param>
/// <param name="ByteLength">The source length, when it was read.</param>
/// <param name="DestinationKey">The tenant-scoped key, when one could be derived.</param>
/// <param name="Reason">Operator-facing detail for anything that is not a plain success.</param>
public sealed record DocumentMigrationItem(
    string RecordType,
    int RecordId,
    int BusinessId,
    DocumentCategory Category,
    string StoredFileName,
    DocumentMigrationStatus Status,
    DocumentSourceLocation? Source = null,
    long? ByteLength = null,
    string? DestinationKey = null,
    string Reason = "")
{
    /// <summary>
    /// Whether this item needs a human before the migration can be called finished. A dry run's
    /// <see cref="DocumentMigrationStatus.Pending"/> does not: it is what a dry run is for.
    /// </summary>
    public bool IsUnresolved => Status is DocumentMigrationStatus.MissingSource
        or DocumentMigrationStatus.Collision
        or DocumentMigrationStatus.InvalidBusiness
        or DocumentMigrationStatus.Failed;
}

/// <summary>
/// The complete outcome of one <c>migrate-documents</c> invocation.
///
/// Every candidate record appears exactly once, so the status counts always sum to
/// <see cref="Candidates"/> and an operator can see that nothing was quietly dropped.
/// </summary>
public sealed record DocumentMigrationReport(bool DryRun, IReadOnlyList<DocumentMigrationItem> Items)
{
    /// <summary>Database records naming a stored document.</summary>
    public int Candidates => Items.Count;

    public int Pending => Count(DocumentMigrationStatus.Pending);

    public int Migrated => Count(DocumentMigrationStatus.Migrated);

    public int AlreadyPresent => Count(DocumentMigrationStatus.AlreadyPresent);

    public int DuplicateReferences => Count(DocumentMigrationStatus.DuplicateReference);

    public int MissingSource => Count(DocumentMigrationStatus.MissingSource);

    public int Collision => Count(DocumentMigrationStatus.Collision);

    public int InvalidBusiness => Count(DocumentMigrationStatus.InvalidBusiness);

    public int Failed => Count(DocumentMigrationStatus.Failed);

    /// <summary>Documents found in protected storage. Reported for the fallback-retirement decision.</summary>
    public int ProtectedSource => Items.Count(item => item.Source == DocumentSourceLocation.ProtectedStorage);

    /// <summary>
    /// Documents found only in the legacy web root. While this is above zero the legacy fallback
    /// is still load-bearing for something.
    /// </summary>
    public int LegacySource => Items.Count(item => item.Source == DocumentSourceLocation.LegacyWebRoot);

    /// <summary>Items a human has to deal with before the migration is finished.</summary>
    public IReadOnlyList<DocumentMigrationItem> Unresolved =>
        Items.Where(item => item.IsUnresolved).ToList();

    /// <summary>
    /// The process exit code: <c>0</c> when the run completed with nothing unresolved, <c>1</c>
    /// when any document is missing its source, collided, carries no usable business, or failed.
    ///
    /// A dry run obeys the same rule, so an operator can gate an apply on it: a clean dry run
    /// exits <c>0</c> whether it found nothing to do or thirty-five documents to copy, and a dry
    /// run that found a collision exits <c>1</c> before anything has been written.
    /// </summary>
    public int ExitCode => Unresolved.Count > 0 ? 1 : 0;

    private int Count(DocumentMigrationStatus status) => Items.Count(item => item.Status == status);
}
