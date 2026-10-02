using Inventory.Application.Documents;
using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The purchase creation use case. Validates the file, the supplier, the items, and the purchase
/// date against inventory-cost transition baselines - in that order, exactly as the former
/// <c>InventoryApi.Services.PurchaseService.Upload</c> did, so an invalid request never reaches
/// document storage - then saves the document and persists the purchase, deleting the document
/// again if persistence fails.
/// </summary>
public sealed class UploadPurchase
{
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic"];
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private readonly IPurchaseStore _store;
    private readonly IDocumentStorage _documents;

    public UploadPurchase(IPurchaseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    public async Task<PurchaseRecord?> Handle(
        PurchaseFileInput file,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
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

        var productIds = items.Select(item => item.ProductId).Distinct().ToList();
        if (productIds.Count > 0 && !await _store.AllProductsExistAsync(productIds, cancellationToken))
            throw new InvalidOperationException("One or more purchase products do not exist.");

        var effectivePurchaseDate = fields.PurchaseDate ?? DateTime.UtcNow;
        var conflict = await _store.FindConflictingCostTransitionBaselineAsync(productIds, effectivePurchaseDate, cancellationToken);
        if (conflict is { } conflictingBaseline)
            throw new InvalidOperationException(
                PurchaseCostTransitionPolicy.DateBeforeCutoffMessage(conflictingBaseline.CutoffAt, conflictingBaseline.ProductId));

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
            return await _store.CreateAsync(resolvedFields, items, fileMetadata, cancellationToken);
        }
        catch
        {
            await _documents.DeleteAsync(DocumentCategory.PurchaseDocument, storedFileName, cancellationToken);
            throw;
        }
    }

    private static PurchaseItemCandidate ToCandidate(PurchaseItemInput item) =>
        new(item.ProductId, item.Quantity, item.UnitCost);
}
