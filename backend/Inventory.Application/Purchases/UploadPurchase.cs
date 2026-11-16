using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.SupplierOrders;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Purchases;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Purchases;

/// <summary>
/// The purchase creation use case. Validates the file, the supplier, the items, and the purchase
/// date against inventory-cost transition baselines - in that order, exactly as the former
/// <c>InventoryApi.Services.PurchaseService.Upload</c> did, so an invalid request never reaches
/// document storage - then saves the document and persists the purchase, deleting the document
/// again if persistence fails.
///
/// <para><strong>The document is optional since issue #976</strong> (child of #965): a business
/// with no receipt to attach, or no storage left to put one in, must still be able to record what
/// it bought. A <c>null</c> file stores no document and no document metadata, and changes nothing
/// else - the supplier, item, GST and cost-transition validation, the stock movements, the
/// allocations and the cost rebuild are identical either way. With a file, every rule is the one
/// that was already here, now stated once in
/// <see cref="PurchaseDocumentPolicy"/> so the create and
/// <see cref="AttachPurchaseDocument"/> cannot drift apart. The one thing a purchase with no
/// document cannot do is fall back to the uploaded file's name for its title, so a blank title is
/// refused instead of stored empty.</para>
///
/// <para><strong>Since issue #968 the document's storage is reserved before it is written.</strong>
/// The reservation is taken after every other validation and before the bytes reach storage, so an
/// upload that does not fit the business's plan allowance is refused without writing anything, and
/// two concurrent uploads cannot both pass a check only one of them fits through. It is consumed
/// inside <see cref="IPurchaseStore.CreateAsync"/>'s own transaction, so the reserved bytes become
/// the document's recorded size with the purchase; a failure releases it along with the bytes.
/// Only the <em>document</em> is ever refused for storage - the purchase itself can always be
/// recorded without one - and while no plan is enforced nothing is reserved at all.</para>
///
/// <para>Since issue #974 the request may also carry the operator's decision to complete a linked
/// supplier order with whatever stays undelivered. This use case adds only the attribution a client
/// may not supply - who decided, and when - and hands the decision to the same
/// <see cref="IPurchaseStore.CreateAsync"/> transaction the purchase, its allocations, its stock
/// movements and its cost rebuild are already written in, so the completion commits with them or not
/// at all. Nothing here closes an order, and there is no second request that could.</para>
///
/// <para><strong>Since issue #1247 the request may carry an idempotency key, and a retry under one
/// saves nothing twice.</strong> The key is checked here, before the storage reservation and before
/// a single byte of the document is written, so an ordinary retry costs no storage at all: a key
/// that already saved this same request answers with that purchase, and a key that saved a
/// <em>different</em> request is refused with
/// <see cref="PurchaseIdempotencyPolicy.KeyReusedMessage"/>. That check is not the guarantee,
/// though - it is the cheap half of it. The authoritative claim is
/// <see cref="IPurchaseStore.CreateAsync"/>'s, taken inside the write transaction, because two
/// copies of one submission that arrive together both read "nothing under this key" here. When that
/// claim replays, this use case removes the bytes and the reservation its own attempt had staged,
/// exactly as a failed save does - the purchase that was saved keeps its own document, and this
/// request leaves nothing behind. A create with no key behaves exactly as it always has.</para>
/// </summary>
public sealed class UploadPurchase
{
    private readonly IPurchaseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;
    private readonly AttachmentQuotaGuard _quota;

    public UploadPurchase(
        IPurchaseStore store,
        IDocumentStorage documents,
        IAuthenticatedActorAccessor actors,
        IClock clock,
        AttachmentQuotaGuard quota)
    {
        _store = store;
        _documents = documents;
        _actors = actors;
        _clock = clock;
        _quota = quota;
    }

    public async Task<PurchaseRecord?> Handle(
        PurchaseFileInput? file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        CancellationToken cancellationToken)
        => await Handle(file, fields, items, supplierOrderCompletion: null, cancellationToken);

    public async Task<PurchaseRecord?> Handle(
        PurchaseFileInput? file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        SupplierOrderCompletionRequest? supplierOrderCompletion,
        CancellationToken cancellationToken)
        => await Handle(file, fields, items, supplierOrderCompletion, idempotencyKey: null, cancellationToken);

    public async Task<PurchaseRecord?> Handle(
        PurchaseFileInput? file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        SupplierOrderCompletionRequest? supplierOrderCompletion,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // The document's own rules, unchanged, and only when there is a document (issue #976).
        if (IsDocumentInvalid(file)) return null;

        // Nothing stands in for the uploaded file's name when there is no uploaded file, so a
        // purchase with no document has to be named. Refused before anything is validated further,
        // because it is a property of the request rather than of the data it refers to.
        if (file is null && string.IsNullOrWhiteSpace(fields.Title))
            throw new InvalidOperationException(PurchaseDocumentPolicy.TitleRequiredWithoutDocumentMessage);

        if (fields.SupplierId.HasValue && !await _store.SupplierExistsAsync(fields.SupplierId.Value, cancellationToken))
            return null;

        if (PurchaseItemFormatPolicy.HasInvalidItem(items.Select(ToCandidate)))
            throw new InvalidOperationException(PurchaseItemFormatPolicy.InvalidItemsMessage);

        // Before the document is saved, so an unsupported classification stores nothing at all.
        PurchaseGstSubmission.EnsureClassificationsAreSupported(fields, items);

        // Before the document is saved too, for the same reason: an over-long invoice number is
        // refused with nothing written, and the number that is accepted is stored trimmed, with
        // blank stored as none (issue #1162).
        fields = PurchaseInvoiceNumberSubmission.Normalized(fields);

        // The key is read from the request as it stands here - the normalised invoice number
        // included, and the submitted purchase date rather than the clock's default, so two
        // identical retries render identically. Over-long is refused before anything is written.
        var idempotency = PurchaseIdempotencySubmission.Claim(
            idempotencyKey, ToFingerprintContent(file, fields, items, supplierOrderCompletion));

        // The cheap half of the guarantee: a retry that arrives after the first save is answered
        // here, before any storage is reserved and before any byte is written. The authoritative
        // claim is the store's, inside its write transaction.
        if (idempotency is not null
            && await _store.FindByIdempotencyKeyAsync(idempotency.Key, cancellationToken) is { } recorded)
        {
            return Replay(idempotency, recorded);
        }

        // Resolved before the document is saved too, and before the write transaction opens: a
        // completion that cannot name who decided it is not auditable, so an unidentifiable caller
        // is refused rather than recorded as blank.
        var decision = supplierOrderCompletion is null ? null : ToDecision(supplierOrderCompletion);

        var productIds = items.Select(item => item.ProductId).Distinct().ToList();
        if (productIds.Count > 0 && !await _store.AllProductsExistAsync(productIds, cancellationToken))
            throw new InvalidOperationException("One or more purchase products do not exist.");

        // The same UTC instant the host clock used to supply directly, now through the port so the
        // default is deterministic and testable; the persisted value for a given instant is unchanged.
        var effectivePurchaseDate = fields.PurchaseDate ?? _clock.UtcNow;
        var conflict = await _store.FindConflictingCostTransitionBaselineAsync(productIds, effectivePurchaseDate, cancellationToken);
        if (conflict is { } conflictingBaseline)
            throw new InvalidOperationException(
                PurchaseCostTransitionPolicy.DateBeforeCutoffMessage(
                    conflictingBaseline.CutoffAt, conflictingBaseline.ProductId, conflictingBaseline.ProductName));

        // The document's storage is reserved before a single byte is written (issue #968), and the
        // refusal is about the document alone: a purchase with no document is always recordable, so
        // the caller is told to save it without one rather than losing what it typed.
        var quota = file is null
            ? AttachmentQuotaDecision.NotEnforced
            : await _quota.ReserveAsync(DocumentCategory.PurchaseDocument, null, file.Length, cancellationToken);
        if (quota.Refused)
            throw new AttachmentStorageQuotaExceededException();

        var (storedFileName, fileMetadata) = await SaveDocumentAsync(file, cancellationToken);
        var staged = new StagedDocument(storedFileName, fileMetadata, quota);

        var resolvedFields = fields with
        {
            // The file-name fallback only has something to fall back to when there is a file; a
            // blank title with no document was already refused above.
            Title = string.IsNullOrWhiteSpace(fields.Title) ? file?.FileName : fields.Title,
            PurchaseDate = effectivePurchaseDate,
        };

        return await CreateOrCleanUpAsync(resolvedFields, items, decision, idempotency, staged, cancellationToken);
    }

    /// <summary>
    /// What this attempt put in place for the document before the purchase was written: the name
    /// the bytes were stored under, the metadata the purchase records them by, and the storage
    /// reserved for them (issue #968). They are staged together and, when no purchase ends up
    /// naming them, removed together, so they travel as one value rather than as three parameters.
    /// A purchase with no document stages nothing and carries the not-enforced decision.
    /// </summary>
    private sealed record StagedDocument(
        string? StoredFileName,
        PurchaseFileMetadata? Metadata,
        AttachmentQuotaDecision Quota);

    /// <summary>
    /// The purchase an earlier request under this key already saved - or a refusal, when that key
    /// saved something else. Nothing is written either way.
    /// </summary>
    /// <exception cref="DomainConflictException">
    /// The key has already saved a different purchase (issue #1247).
    /// </exception>
    private static PurchaseRecord Replay(PurchaseIdempotencyClaim claim, PurchaseIdempotentCreate recorded) =>
        PurchaseIdempotencySubmission.IsSameRequest(claim, recorded)
            ? recorded.Purchase
            : throw new DomainConflictException(PurchaseIdempotencyPolicy.KeyReusedMessage);

    /// <summary>
    /// The request as <see cref="PurchaseCreateFingerprint"/> renders it: what the caller submitted,
    /// including the document's name and length and the supplier-order completion it asked for, and
    /// never a value this server resolved for it.
    /// </summary>
    private static PurchaseFingerprintContent ToFingerprintContent(
        PurchaseFileInput? file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        SupplierOrderCompletionRequest? supplierOrderCompletion) =>
        new(
            fields.Title,
            fields.Notes,
            fields.InvoiceNumber,
            fields.TotalAmount,
            fields.DeliveryCost,
            fields.DeliveryGstClassification,
            fields.PackageCost,
            fields.PackageGstClassification,
            fields.PurchaseDate,
            fields.SupplierId,
            file?.FileName,
            file?.Length,
            supplierOrderCompletion?.SupplierOrderId,
            supplierOrderCompletion?.DecisionFingerprint,
            [.. items.Select(item => new PurchaseFingerprintLine(
                item.ProductId, item.Quantity, item.UnitCost, item.GstClassification))]);

    private static bool IsDocumentInvalid(PurchaseFileInput? file) =>
        file is not null
        && (!PurchaseDocumentPolicy.IsSizeValid(file.Length) || !PurchaseDocumentPolicy.IsExtensionAllowed(ExtensionOf(file)));

    private static string? ExtensionOf(PurchaseFileInput? file) =>
        file is null ? null : Path.GetExtension(file.FileName).ToLowerInvariant();

    private async Task<(string? StoredFileName, PurchaseFileMetadata? Metadata)> SaveDocumentAsync(
        PurchaseFileInput? file, CancellationToken cancellationToken)
    {
        if (file is null) return (null, null);

        var storedFileName = $"{Guid.NewGuid()}{ExtensionOf(file)}";
        await using (var source = file.OpenReadStream())
        {
            await _documents.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, source, cancellationToken);
        }

        var metadata = new PurchaseFileMetadata(storedFileName, file.ContentType ?? string.Empty, file.FileName, file.Length);
        return (storedFileName, metadata);
    }

    private async Task<PurchaseRecord?> CreateOrCleanUpAsync(
        PurchaseFields resolvedFields,
        IReadOnlyList<PurchaseItemInput> items,
        SupplierOrderCompletionDecision? decision,
        PurchaseIdempotencyClaim? idempotency,
        StagedDocument staged,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _store.CreateAsync(
                resolvedFields, items, staged.Metadata, decision, staged.Quota.Reservation, idempotency, cancellationToken);
            if (!outcome.Replayed) return outcome.Purchase;

            // An earlier request under this key had already saved the purchase, and the store wrote
            // nothing (issue #1247). The bytes this attempt stored and the storage it reserved are
            // therefore for a document no purchase will ever name, so they go exactly as they do
            // when a save fails - the saved purchase keeps whatever document it was saved with.
            await CleanUpAsync(staged, cancellationToken);
            return outcome.Purchase;
        }
        catch
        {
            // Only bytes this request wrote are removed; with no document there are none, and the
            // purchase that failed to save is simply not there. The reservation goes with them -
            // including when the write refused the upload because the reservation had expired and
            // the quota no longer fits - so nothing holds storage for a purchase that was not
            // saved. A failed removal is left for the reconciliation command rather than hiding
            // the failure that produced it.
            await CleanUpAsync(staged, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Removes what this attempt staged for a document that is not going to be recorded: the bytes
    /// it wrote, and the storage it reserved for them. Only this request's own bytes are ever
    /// touched.
    /// </summary>
    private async Task CleanUpAsync(StagedDocument staged, CancellationToken cancellationToken)
    {
        if (staged.StoredFileName is not null)
            await _documents.DeleteAsync(DocumentCategory.PurchaseDocument, staged.StoredFileName, cancellationToken);
        await _quota.ReleaseAsync(staged.Quota, cancellationToken);
    }

    private SupplierOrderCompletionDecision ToDecision(SupplierOrderCompletionRequest request)
    {
        var actor = _actors.GetCurrentActor();
        var identity = actor.Actor
            ?? throw new BusinessAccessDeniedException(actor.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);

        return new SupplierOrderCompletionDecision(
            request.SupplierOrderId,
            request.DecisionFingerprint,
            identity.DirectoryTenantId,
            identity.ObjectId,
            _clock.UtcNow);
    }

    private static PurchaseItemCandidate ToCandidate(PurchaseItemInput item) =>
        new(item.ProductId, item.Quantity, item.UnitCost);
}
