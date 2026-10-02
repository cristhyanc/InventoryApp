using Inventory.Application.Documents;

namespace Inventory.Application.Purchases;

public sealed record PurchaseFileContent(byte[] Content, string ContentType, string FileName);

public sealed class GetPurchaseFile
{
    private readonly IPurchaseStore _store;
    private readonly IDocumentStorage _documents;

    public GetPurchaseFile(IPurchaseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    /// <summary>
    /// Resolves the tenant-owned purchase first: a missing purchase and a missing document are
    /// both an ordinary <c>null</c>, never distinguished, exactly like the retired service.
    /// </summary>
    public async Task<PurchaseFileContent?> Handle(int id, CancellationToken cancellationToken)
    {
        var metadata = await _store.FindFileMetadataAsync(id, cancellationToken);
        if (metadata is null) return null;

        await using var document = await _documents.OpenReadAsync(DocumentCategory.PurchaseDocument, metadata.StoredFileName, cancellationToken);
        if (document is null) return null;

        using var buffer = new MemoryStream();
        await document.Content.CopyToAsync(buffer, cancellationToken);
        return new PurchaseFileContent(buffer.ToArray(), metadata.ContentType, metadata.FileName);
    }
}
