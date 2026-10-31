using Inventory.Application.Attachments;
using Inventory.Application.Purchases;
using Inventory.Application.SupplierOrders;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite. See <c>EfPurchaseStoreTests</c> for behaviour that needs
/// a real transaction/query.
/// </summary>
public sealed class FakePurchaseStore : IPurchaseStore
{
    private readonly Dictionary<int, PurchaseRecord> _records = [];
    private int _nextId = 1;

    public bool SupplierExists { get; set; } = true;
    public bool AllProductsExist { get; set; } = true;
    public (long ProductId, string? ProductName, DateTime CutoffAt)? ConflictingBaseline { get; set; }
    public bool ThrowOnCreate { get; set; }
    public bool ThrowOnAttach { get; set; }

    /// <summary>
    /// Makes the write refuse the upload the way a commit does when it finds its reservation
    /// expired and the quota no longer satisfied (issue #968), so a use-case test can see what the
    /// orchestration does with that answer.
    /// </summary>
    public bool QuotaExceededOnCreate { get; set; }

    /// <summary>The same, for the attach-a-document-later path.</summary>
    public bool QuotaExceededOnAttach { get; set; }

    /// <summary>The storage reservation each write was handed, or <c>null</c> when it was handed none.</summary>
    public AttachmentUploadReservation? LastCreateReservation { get; private set; }

    /// <summary>The storage reservation the attach was handed.</summary>
    public AttachmentUploadReservation? LastAttachReservation { get; private set; }

    public PurchaseRecord? LastCreated { get; private set; }

    /// <summary>The metadata the attach use case handed the store, so a test can see the stored name it generated.</summary>
    public PurchaseFileMetadata? LastAttachedDocument { get; private set; }

    /// <summary>
    /// The supplier-order completion decision the use case handed the store (issue #974), so a test
    /// can see the attribution the Application resolved without a database. The store itself applies
    /// nothing: closing an order is the EF transaction's work, covered by <c>EfPurchaseStoreTests</c>.
    /// </summary>
    public SupplierOrderCompletionDecision? LastCompletionDecision { get; private set; }

    /// <summary>
    /// The effective purchase date the use case checked against the cost-transition baselines, so a
    /// test can see the date the validation used and not only the date that was persisted.
    /// </summary>
    public DateTime? LastConflictCheckedPurchaseDate { get; private set; }

    public Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken)
    {
        IEnumerable<PurchaseRecord> query = _records.Values;
        if (supplierId.HasValue) query = query.Where(x => x.SupplierId == supplierId);
        return Task.FromResult<IReadOnlyList<PurchaseRecord>>(query.ToList());
    }

    public Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault(id));

    /// <summary>
    /// A purchase that does not exist and a purchase with no document are the same <c>null</c>, as
    /// the real adapter answers both (issue #976).
    /// </summary>
    public Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault(id) is
        { StoredFileName: not null, ContentType: not null, FileName: not null, FileSizeBytes: not null } record
            ? new PurchaseFileMetadata(
                record.StoredFileName, record.ContentType, record.FileName, record.FileSizeBytes.Value)
            : null);

    public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) => Task.FromResult(SupplierExists);

    public Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
        Task.FromResult(AllProductsExist);

    public Task<(long ProductId, string? ProductName, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken)
    {
        LastConflictCheckedPurchaseDate = purchaseDate;
        return Task.FromResult(ConflictingBaseline);
    }

    public Task<PurchaseRecord> CreateAsync(
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        PurchaseFileMetadata? file,
        SupplierOrderCompletionDecision? supplierOrderCompletion,
        AttachmentUploadReservation? attachmentReservation,
        CancellationToken cancellationToken)
    {
        LastCompletionDecision = supplierOrderCompletion;
        LastCreateReservation = attachmentReservation;
        if (QuotaExceededOnCreate) throw new AttachmentStorageQuotaExceededException();
        if (ThrowOnCreate) throw new InvalidOperationException("store failure");

        var id = _nextId++;
        // The GST classifications are resolved by the same Domain rules the EF store uses, so a use-case
        // test sees the states a real create would persist (issue #429).
        var delivery = PurchaseGstPolicy.ClassifyCharge(
            fields.DeliveryCost, PurchaseGstPolicy.Classify(fields.DeliveryGstClassification));
        var package = PurchaseGstPolicy.ClassifyCharge(
            fields.PackageCost, PurchaseGstPolicy.Classify(fields.PackageGstClassification));
        var record = new PurchaseRecord(
            id, 1, fields.Title ?? string.Empty, fields.Notes, fields.TotalAmount,
            fields.DeliveryCost, delivery.Classification, delivery.Source,
            fields.PackageCost, package.Classification, package.Source,
            fields.PurchaseDate ?? DateTime.UtcNow, fields.SupplierId, null,
            items.Select(item =>
            {
                var state = PurchaseGstPolicy.Classify(item.GstClassification);
                return new PurchaseItemRecord(
                    0, id, item.ProductId, item.Quantity, item.UnitCost, state.Classification, state.Source, null);
            }).ToList(),
            file?.FileName, file?.StoredFileName, file?.ContentType, file?.FileSizeBytes, DateTime.UtcNow);
        _records[id] = record;
        LastCreated = record;
        return Task.FromResult(record);
    }

    /// <summary>
    /// The in-memory half of "attach only if empty" (issue #976). The real serialisation is the EF
    /// adapter's write lock, covered by <c>EfPurchaseStoreTests</c>; what a use-case test needs from
    /// here is the three outcomes and the record an attach produces.
    /// </summary>
    public Task<PurchaseDocumentAttachment> AttachDocumentAsync(
        int id,
        PurchaseFileMetadata document,
        AttachmentUploadReservation? attachmentReservation,
        CancellationToken cancellationToken)
    {
        LastAttachedDocument = document;
        LastAttachReservation = attachmentReservation;
        if (QuotaExceededOnAttach) throw new AttachmentStorageQuotaExceededException();
        if (ThrowOnAttach) throw new InvalidOperationException("store failure");

        if (!_records.TryGetValue(id, out var existing))
            return Task.FromResult(new PurchaseDocumentAttachment(PurchaseDocumentAttachOutcome.PurchaseNotFound, null));

        if (existing.StoredFileName is not null)
            return Task.FromResult(new PurchaseDocumentAttachment(PurchaseDocumentAttachOutcome.AlreadyHasDocument, null));

        var attached = existing with
        {
            FileName = document.FileName,
            StoredFileName = document.StoredFileName,
            ContentType = document.ContentType,
            FileSizeBytes = document.FileSizeBytes,
        };
        _records[id] = attached;
        return Task.FromResult(new PurchaseDocumentAttachment(PurchaseDocumentAttachOutcome.Attached, attached));
    }

    /// <summary>Seeds a stored purchase so an attach has something to attach to.</summary>
    public PurchaseRecord AddPurchase(PurchaseFileMetadata? document = null)
    {
        var id = _nextId++;
        var record = new PurchaseRecord(
            id, 1, "Weekly restock", null, null, null, GstClassification.Unknown, GstClassificationSource.Unknown,
            null, GstClassification.Unknown, GstClassificationSource.Unknown, DateTime.UtcNow, null, null, [],
            document?.FileName, document?.StoredFileName, document?.ContentType, document?.FileSizeBytes,
            DateTime.UtcNow);
        _records[id] = record;
        return record;
    }

    public Task<PurchaseRecord?> UpdateAsync(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not needed by these tests; see EfPurchaseStoreTests for Update behaviour.");

    public Task<string?> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        if (!_records.Remove(id, out var existing)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(existing.StoredFileName);
    }
}
