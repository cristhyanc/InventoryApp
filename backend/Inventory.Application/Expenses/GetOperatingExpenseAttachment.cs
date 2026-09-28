using Inventory.Application.Documents;

namespace Inventory.Application.Expenses;

public sealed class GetOperatingExpenseAttachment
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;

    public GetOperatingExpenseAttachment(IOperatingExpenseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    /// <summary>
    /// Resolves the tenant-owned expense first, exactly like the retired controller did: a
    /// missing expense and a missing document are both an ordinary <c>null</c>, never distinguished.
    /// </summary>
    public async Task<OperatingExpenseAttachmentResult?> Handle(int id, CancellationToken cancellationToken)
    {
        var expense = await _store.FindByIdAsync(id, cancellationToken);
        if (expense?.AttachmentStoredFileName is null) return null;

        var document = await _documents.OpenReadAsync(DocumentCategory.ExpenseAttachment, expense.AttachmentStoredFileName, cancellationToken);
        if (document is null) return null;

        return new OperatingExpenseAttachmentResult(document, expense.AttachmentFileName, expense.AttachmentContentType);
    }
}
