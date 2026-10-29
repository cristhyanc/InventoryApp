using Inventory.Application.Documents;
using Inventory.Application.SupplierOrders;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
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
/// <para>Since issue #974 the request may also carry the operator's decision to complete a linked
/// supplier order with whatever stays undelivered. This use case adds only the attribution a client
/// may not supply - who decided, and when - and hands the decision to the same
/// <see cref="IPurchaseStore.CreateAsync"/> transaction the purchase, its allocations, its stock
/// movements and its cost rebuild are already written in, so the completion commits with them or not
/// at all. Nothing here closes an order, and there is no second request that could.</para>
/// </summary>
public sealed class UploadPurchase
{
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic"];
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private readonly IPurchaseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public UploadPurchase(
        IPurchaseStore store, IDocumentStorage documents, IAuthenticatedActorAccessor actors, IClock clock)
    {
        _store = store;
        _documents = documents;
        _actors = actors;
        _clock = clock;
    }

    public async Task<PurchaseRecord?> Handle(
        PurchaseFileInput file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        CancellationToken cancellationToken)
        => await Handle(file, fields, items, supplierOrderCompletion: null, cancellationToken);

    public async Task<PurchaseRecord?> Handle(
        PurchaseFileInput file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        SupplierOrderCompletionRequest? supplierOrderCompletion,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0) return null;
        if (file.Length > MaxFileSizeBytes) return null;

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension)) return null;

        if (fields.SupplierId.HasValue && !await _store.SupplierExistsAsync(fields.SupplierId.Value, cancellationToken))
            return null;

        if (PurchaseItemFormatPolicy.HasInvalidItem(items.Select(ToCandidate)))
            throw new InvalidOperationException(PurchaseItemFormatPolicy.InvalidItemsMessage);

        // Before the document is saved, so an unsupported classification stores nothing at all.
        PurchaseGstSubmission.EnsureClassificationsAreSupported(fields, items);

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

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        await using (var source = file.OpenReadStream())
        {
            await _documents.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, source, cancellationToken);
        }

        var fileMetadata = new PurchaseFileMetadata(storedFileName, file.ContentType ?? string.Empty, file.FileName, file.Length);
        var resolvedFields = fields with
        {
            Title = string.IsNullOrWhiteSpace(fields.Title) ? file.FileName : fields.Title,
            PurchaseDate = effectivePurchaseDate,
        };

        try
        {
            return await _store.CreateAsync(resolvedFields, items, fileMetadata, decision, cancellationToken);
        }
        catch
        {
            await _documents.DeleteAsync(DocumentCategory.PurchaseDocument, storedFileName, cancellationToken);
            throw;
        }
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
