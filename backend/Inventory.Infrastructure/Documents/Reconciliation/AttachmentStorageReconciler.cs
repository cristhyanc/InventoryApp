using Inventory.Application.Documents;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Documents.Reconciliation;

/// <summary>
/// Reconciles what is in document storage against the purchase and expense records that refer to
/// it (issue #968), for the human-invoked <c>reconcile-attachment-storage</c> command.
///
/// <para><strong>Why it exists.</strong> A document's bytes live outside the database, so the two
/// can disagree in exactly three ways and the quota deliberately resolves all three in the
/// business's favour: a file nothing refers to is charged to nobody, a record whose file is gone
/// charges for storage that is not there, and a record whose size was never recorded charges
/// nothing at all. None of those is visible from inside the application, so this is the operator's
/// read of the truth.</para>
///
/// <para><strong>It never deletes.</strong> An apply writes one thing and one thing only: a size
/// that was never recorded, taken from the stored file itself. An orphan file is reported, never
/// removed - deleting a business's receipt is not recoverable by rolling a transaction back, and
/// automatic orphan deletion is explicitly out of this issue's scope. A missing file is reported,
/// never invented, and the record that refers to it is left exactly as it is.</para>
///
/// <para><strong>It is idempotent and safely restartable.</strong> A second run finds the sizes
/// the first one filled already recorded, reports the same orphans and missing files, and writes
/// nothing new. A run that is interrupted has committed whichever sizes it had already filled, and
/// re-running continues from there.</para>
///
/// <para><strong>It never guesses.</strong> A size is filled only from a file the storage could
/// actually be read for; one that cannot be read stays unknown and is reported as unknown. Usage
/// is summed from records, never from storage, so nothing here changes what a business is charged
/// beyond recording a size that was always that file's size.</para>
///
/// <para>It reads every business deliberately, through the unrestricted context the command opens -
/// the same decision <c>DocumentMigrator</c> makes, for the same reason, and it is a command rather
/// than a request path.</para>
/// </summary>
public static class AttachmentStorageReconciler
{
    /// <summary>Runs the reconciliation.</summary>
    /// <param name="db">An unrestricted context: the report is per business, across all of them.</param>
    /// <param name="files">The read-only inspection of what storage actually holds.</param>
    /// <param name="apply">
    /// <see langword="false"/> writes nothing at all and reports what an apply would fill in.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads, the measurements and the writes.</param>
    public static async Task<AttachmentStorageReconciliationReport> RunAsync(
        AppDbContext db,
        IAttachmentFileInventory files,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(files);

        var attachments = await ReadAttachmentsAsync(db, cancellationToken).ConfigureAwait(false);
        var businessIds = await db.Businesses
            .AsNoTracking()
            .Select(business => business.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findings = new Dictionary<int, List<AttachmentStorageFinding>>();
        var unattributed = new List<AttachmentStorageFinding>();

        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var measured = await files
                .MeasureAsync(attachment.Category, attachment.BusinessId, attachment.StoredFileName, cancellationToken)
                .ConfigureAwait(false);

            if (measured is null)
            {
                Add(findings, attachment.BusinessId, new AttachmentStorageFinding(
                    AttachmentStorageFindingKind.MissingFile,
                    attachment.Category,
                    attachment.RecordId,
                    attachment.StoredFileName,
                    ByteLength: null));
                continue;
            }

            if (attachment.SizeBytes is not null) continue;

            if (!apply)
            {
                Add(findings, attachment.BusinessId, new AttachmentStorageFinding(
                    AttachmentStorageFindingKind.UnknownSize,
                    attachment.Category,
                    attachment.RecordId,
                    attachment.StoredFileName,
                    measured));
                continue;
            }

            await FillSizeAsync(db, attachment, measured.Value, cancellationToken).ConfigureAwait(false);
            Add(findings, attachment.BusinessId, new AttachmentStorageFinding(
                AttachmentStorageFindingKind.FilledSize,
                attachment.Category,
                attachment.RecordId,
                attachment.StoredFileName,
                measured));
        }

        await AddOrphansAsync(files, attachments, businessIds, findings, unattributed, cancellationToken)
            .ConfigureAwait(false);

        var businesses = businessIds
            .Select(businessId => Report(businessId, attachments, findings))
            .Where(report => report.Attachments > 0 || report.Findings.Count > 0)
            .OrderBy(report => report.BusinessId)
            .ToList();

        return new AttachmentStorageReconciliationReport(!apply, businesses, unattributed);
    }

    /// <summary>
    /// Every stored file that no record refers to. On a per-business layout each one belongs to the
    /// business whose prefix it was listed under; on the filesystem layout nobody can say whose it
    /// is, so it is reported separately rather than charged to a business that may not own it.
    /// </summary>
    private static async Task AddOrphansAsync(
        IAttachmentFileInventory files,
        IReadOnlyList<AttachmentRow> attachments,
        IReadOnlyList<int> businessIds,
        Dictionary<int, List<AttachmentStorageFinding>> findings,
        List<AttachmentStorageFinding> unattributed,
        CancellationToken cancellationToken)
    {
        foreach (var category in new[] { DocumentCategory.PurchaseDocument, DocumentCategory.ExpenseAttachment })
        {
            var referenced = attachments
                .Where(attachment => attachment.Category == category)
                .Select(attachment => attachment.StoredFileName)
                .ToHashSet(StringComparer.Ordinal);

            var stored = await files.ListAsync(category, businessIds, cancellationToken).ConfigureAwait(false);

            foreach (var file in stored.Where(file => !referenced.Contains(file.StoredFileName)))
            {
                var finding = new AttachmentStorageFinding(
                    AttachmentStorageFindingKind.OrphanFile,
                    category,
                    RecordId: null,
                    file.StoredFileName,
                    file.ByteLength);

                if (file.BusinessId is { } businessId)
                {
                    Add(findings, businessId, finding);
                }
                else
                {
                    unattributed.Add(finding);
                }
            }
        }
    }

    private static AttachmentStorageBusinessReport Report(
        int businessId,
        IReadOnlyList<AttachmentRow> attachments,
        Dictionary<int, List<AttachmentStorageFinding>> findings)
    {
        var owned = attachments.Where(attachment => attachment.BusinessId == businessId).ToList();

        return new AttachmentStorageBusinessReport(
            businessId,
            owned.Count,
            owned.Sum(attachment => attachment.SizeBytes ?? 0),
            findings.TryGetValue(businessId, out var found) ? found : []);
    }

    private static async Task FillSizeAsync(
        AppDbContext db, AttachmentRow attachment, long measured, CancellationToken cancellationToken)
    {
        if (attachment.Category == DocumentCategory.PurchaseDocument)
        {
            var purchase = await db.Receipts
                .FirstAsync(candidate => candidate.Id == attachment.RecordId, cancellationToken)
                .ConfigureAwait(false);
            purchase.FileSizeBytes = measured;
        }
        else
        {
            var expense = await db.OperatingExpenses
                .FirstAsync(candidate => candidate.Id == attachment.RecordId, cancellationToken)
                .ConfigureAwait(false);
            expense.AttachmentFileSizeBytes = measured;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AttachmentRow>> ReadAttachmentsAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        // No IgnoreQueryFilters anywhere here: the command opens an unrestricted context, under
        // which the central tenant filter matches every row by construction. Reaching past the
        // filter is a boundary decision and this is not one.
        var purchases = await db.Receipts
            .AsNoTracking()
            .Where(purchase => purchase.StoredFileName != null)
            .Select(purchase => new AttachmentRow(
                purchase.BusinessId,
                DocumentCategory.PurchaseDocument,
                purchase.Id,
                purchase.StoredFileName!,
                purchase.FileSizeBytes))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var expenses = await db.OperatingExpenses
            .AsNoTracking()
            .Where(expense => expense.AttachmentStoredFileName != null)
            .Select(expense => new AttachmentRow(
                expense.BusinessId,
                DocumentCategory.ExpenseAttachment,
                expense.Id,
                expense.AttachmentStoredFileName!,
                expense.AttachmentFileSizeBytes))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. purchases, .. expenses];
    }

    private static void Add(
        Dictionary<int, List<AttachmentStorageFinding>> findings, int businessId, AttachmentStorageFinding finding)
    {
        if (!findings.TryGetValue(businessId, out var list))
        {
            list = [];
            findings[businessId] = list;
        }

        list.Add(finding);
    }

    /// <summary>One record that refers to a document, as the reconciliation reads it.</summary>
    private sealed record AttachmentRow(
        int BusinessId, DocumentCategory Category, int RecordId, string StoredFileName, long? SizeBytes);
}
