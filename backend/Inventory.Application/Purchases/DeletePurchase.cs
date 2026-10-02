using Inventory.Application.Documents;

namespace Inventory.Application.Purchases;

public sealed class DeletePurchase
{
    private readonly IPurchaseStore _store;
    private readonly IDocumentStorage _documents;

    public DeletePurchase(IPurchaseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    public async Task<bool> Handle(int id, CancellationToken cancellationToken)
    {
        var storedFileName = await _store.DeleteAsync(id, cancellationToken);
        if (storedFileName is null) return false;

        await _documents.DeleteAsync(DocumentCategory.PurchaseDocument, storedFileName, cancellationToken);
        return true;
    }
}
