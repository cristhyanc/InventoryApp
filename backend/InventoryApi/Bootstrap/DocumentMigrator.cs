using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Documents.Migration;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Copies stored documents from the filesystem to tenant-scoped Azure Blob storage
/// (issue #39, checkpoint 3), behind the human-invoked <c>migrate-documents</c> command.
///
/// Three properties shape the whole design:
///
/// <list type="bullet">
///   <item><b>Ownership comes from the database.</b> Each document's business is read from the
///   persisted record that owns it - never from a folder name, a file name, a blob name or the
///   command line. A record with no usable business is refused rather than filed under a
///   default, because the failure mode there is handing one business's document to another.</item>
///   <item><b>Nothing is ever overwritten or deleted.</b> The destination is written only when
///   its key is free, and the source is left exactly where it is. Retiring the filesystem copies
///   is a separate human decision, later, once this has been verified.</item>
///   <item><b>Reruns are cheap and safe.</b> A document already at its destination with the same
///   bytes is skipped, so an interrupted run is finished simply by running it again.</item>
/// </list>
///
/// It takes an unrestricted <see cref="AppDbContext"/> because it must see every business's
/// records at once, and a <see cref="IDocumentMigrationDestination"/> rather than
/// <see cref="IDocumentStorage"/> because that port is scoped to the current request's business
/// and correctly refuses to run unscoped.
/// </summary>
public static class DocumentMigrator
{
    private const string PurchaseRecordType = "Purchase";
    private const string ExpenseRecordType = "OperatingExpense";

    /// <param name="db">An unrestricted context - the migration spans every business.</param>
    /// <param name="source">Filesystem storage, read exactly as the application reads it.</param>
    /// <param name="destination">Where documents are copied to.</param>
    /// <param name="apply">False to inspect and report only, writing nothing anywhere.</param>
    /// <param name="cancellationToken">Cancels the run between documents.</param>
    public static async Task<DocumentMigrationReport> RunAsync(
        AppDbContext db,
        FileSystemDocumentStorage source,
        IDocumentMigrationDestination destination,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var references = await LoadReferencesAsync(db, cancellationToken);
        var items = new List<DocumentMigrationItem>(references.Count);

        // One destination can be named by more than one record. Grouping first means the bytes
        // are inspected and copied once; the other records are still reported, so duplicated
        // metadata is visible rather than quietly collapsed into a single line.
        var claimedDestinations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!BusinessId.TryFrom(reference.BusinessId, out var businessId))
            {
                items.Add(reference.With(
                    DocumentMigrationStatus.InvalidBusiness,
                    reason: $"The record carries business id {reference.BusinessId}, which is not a valid owner. "
                        + "It has not been migrated: a document must never be written under a business that does not own it."));
                continue;
            }

            string destinationKey;
            try
            {
                destinationKey = destination.KeyFor(businessId, reference.Category, reference.StoredFileName);
            }
            catch (ArgumentException failure)
            {
                items.Add(reference.With(
                    DocumentMigrationStatus.Failed,
                    reason: $"The stored document name cannot be used as a destination name: {failure.Message}"));
                continue;
            }

            if (!claimedDestinations.Add(destinationKey))
            {
                items.Add(reference.With(
                    DocumentMigrationStatus.DuplicateReference,
                    destinationKey: destinationKey,
                    reason: "Another record in this run names the same document. The bytes are migrated once; "
                        + "this record is listed so the shared reference is visible."));
                continue;
            }

            items.Add(await MigrateOneAsync(
                reference, businessId, destinationKey, source, destination, apply, cancellationToken));
        }

        return new DocumentMigrationReport(DryRun: !apply, items);
    }

    private static async Task<DocumentMigrationItem> MigrateOneAsync(
        DocumentReference reference,
        BusinessId businessId,
        string destinationKey,
        FileSystemDocumentStorage source,
        IDocumentMigrationDestination destination,
        bool apply,
        CancellationToken cancellationToken)
    {
        var location = source.Locate(reference.Category, reference.StoredFileName);
        if (location is null)
        {
            return reference.With(
                DocumentMigrationStatus.MissingSource,
                destinationKey: destinationKey,
                reason: "The record names a stored document that is in neither protected nor legacy storage. "
                    + "Nothing was written and the record was left unchanged.");
        }

        // Hash the source by streaming it, then open it again to copy. Reading twice costs one
        // extra pass over a small file; holding a document in memory to avoid that would make
        // verification the thing most likely to fail on a large one.
        DocumentFingerprint sourceFingerprint;
        await using (var content = await source.OpenReadAsync(
            reference.Category, reference.StoredFileName, cancellationToken))
        {
            if (content is null)
            {
                return reference.With(
                    DocumentMigrationStatus.MissingSource,
                    location,
                    destinationKey: destinationKey,
                    reason: "The stored document disappeared between being located and being read.");
            }

            sourceFingerprint = await DocumentContentHash.ComputeAsync(content.Content, cancellationToken);
        }

        var existing = await destination.InspectAsync(
            businessId, reference.Category, reference.StoredFileName, cancellationToken);

        if (existing is not null)
        {
            return existing.Matches(sourceFingerprint)
                ? reference.With(
                    DocumentMigrationStatus.AlreadyPresent,
                    location,
                    sourceFingerprint.ByteLength,
                    destinationKey,
                    "The destination already holds these exact bytes.")
                : reference.With(
                    DocumentMigrationStatus.Collision,
                    location,
                    sourceFingerprint.ByteLength,
                    destinationKey,
                    $"The destination already holds a different document: source is {sourceFingerprint.ByteLength} bytes "
                        + $"(sha256 {sourceFingerprint.ShortSha256}), destination is {existing.ByteLength} bytes "
                        + $"(sha256 {existing.ShortSha256}). Nothing was overwritten, renamed or deleted. "
                        + "A human must establish what is already stored there.");
        }

        if (!apply)
        {
            return reference.With(
                DocumentMigrationStatus.Pending,
                location,
                sourceFingerprint.ByteLength,
                destinationKey,
                "The destination is free; an apply would copy this document.");
        }

        return await CopyAndVerifyAsync(
            reference, businessId, destinationKey, location.Value, sourceFingerprint,
            source, destination, cancellationToken);
    }

    private static async Task<DocumentMigrationItem> CopyAndVerifyAsync(
        DocumentReference reference,
        BusinessId businessId,
        string destinationKey,
        DocumentSourceLocation location,
        DocumentFingerprint sourceFingerprint,
        FileSystemDocumentStorage source,
        IDocumentMigrationDestination destination,
        CancellationToken cancellationToken)
    {
        bool uploaded;
        await using (var content = await source.OpenReadAsync(
            reference.Category, reference.StoredFileName, cancellationToken))
        {
            if (content is null)
            {
                return reference.With(
                    DocumentMigrationStatus.MissingSource,
                    location,
                    sourceFingerprint.ByteLength,
                    destinationKey,
                    "The stored document disappeared between being hashed and being copied.");
            }

            uploaded = await destination.UploadIfAbsentAsync(
                businessId, reference.Category, reference.StoredFileName, content.Content, cancellationToken);
        }

        if (!uploaded)
        {
            // Something took the key between the inspection above and this write. Whatever it
            // is, this run did not put it there and must not replace it.
            return reference.With(
                DocumentMigrationStatus.Collision,
                location,
                sourceFingerprint.ByteLength,
                destinationKey,
                "The destination was taken between inspection and copy, so nothing was written. "
                    + "Re-run to compare the two documents.");
        }

        // Verify from the destination's own bytes rather than from the upload call's word for
        // it. A migration's only real claim is that the copy is readable and identical, and that
        // claim has to be tested where the application will actually read it.
        var written = await destination.InspectAsync(
            businessId, reference.Category, reference.StoredFileName, cancellationToken);

        if (written is null)
        {
            return reference.With(
                DocumentMigrationStatus.Failed,
                location,
                sourceFingerprint.ByteLength,
                destinationKey,
                "The copy reported success but nothing is readable at the destination. The source was left untouched.");
        }

        if (!written.Matches(sourceFingerprint))
        {
            return reference.With(
                DocumentMigrationStatus.Failed,
                location,
                sourceFingerprint.ByteLength,
                destinationKey,
                $"The copy does not match its source: source is {sourceFingerprint.ByteLength} bytes "
                    + $"(sha256 {sourceFingerprint.ShortSha256}), destination is {written.ByteLength} bytes "
                    + $"(sha256 {written.ShortSha256}). The source was left untouched and this document is NOT migrated.");
        }

        return reference.With(
            DocumentMigrationStatus.Migrated,
            location,
            sourceFingerprint.ByteLength,
            destinationKey,
            "Copied and verified by size and SHA-256 at the destination.");
    }

    /// <summary>
    /// Every record naming a stored document, in a fixed order so two runs over the same data
    /// produce the same report and the same duplicate-resolution decisions.
    /// </summary>
    private static async Task<List<DocumentReference>> LoadReferencesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var purchases = await db.Receipts.AsNoTracking()
            .Where(purchase => purchase.StoredFileName != null && purchase.StoredFileName != "")
            .OrderBy(purchase => purchase.Id)
            .Select(purchase => new DocumentReference(
                PurchaseRecordType,
                purchase.Id,
                purchase.BusinessId,
                DocumentCategory.PurchaseDocument,
                purchase.StoredFileName))
            .ToListAsync(cancellationToken);

        var expenses = await db.OperatingExpenses.AsNoTracking()
            .Where(expense => expense.AttachmentStoredFileName != null && expense.AttachmentStoredFileName != "")
            .OrderBy(expense => expense.Id)
            .Select(expense => new DocumentReference(
                ExpenseRecordType,
                expense.Id,
                expense.BusinessId,
                DocumentCategory.ExpenseAttachment,
                expense.AttachmentStoredFileName!))
            .ToListAsync(cancellationToken);

        // Whitespace-only names survive the SQL filter above but name nothing; they are dropped
        // here rather than reported, because a record with no document is not a candidate.
        return purchases.Concat(expenses)
            .Where(reference => !string.IsNullOrWhiteSpace(reference.StoredFileName))
            .ToList();
    }

    /// <summary>One database record's claim on a stored document.</summary>
    private sealed record DocumentReference(
        string RecordType,
        int RecordId,
        int BusinessId,
        DocumentCategory Category,
        string StoredFileName)
    {
        public DocumentMigrationItem With(
            DocumentMigrationStatus status,
            DocumentSourceLocation? source = null,
            long? byteLength = null,
            string? destinationKey = null,
            string reason = "") =>
            new(RecordType, RecordId, BusinessId, Category, StoredFileName,
                status, source, byteLength, destinationKey, reason);
    }
}
