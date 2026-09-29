using Inventory.Application.Documents;

namespace Inventory.Application.Expenses;

public sealed class DeleteOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;

    public DeleteOperatingExpense(IOperatingExpenseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    public async Task<bool> Handle(int id, CancellationToken cancellationToken)
    {
        var deleted = await _store.DeleteAsync(id, cancellationToken);
        if (deleted is null) return false;

        if (deleted.AttachmentStoredFileName is not null)
            await _documents.DeleteAsync(DocumentCategory.ExpenseAttachment, deleted.AttachmentStoredFileName, cancellationToken);

        return true;
    }
}
