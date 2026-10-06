using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class DeleteOperatingExpenseTests
{
    private static OperatingExpenseFields Fields() => new(
        new DateTime(2026, 3, 1), ExpenseCategory.Insurance, "Insurance", 10m, 1m, 11m,
        null, null, null, null, null, null);

    [Fact]
    public async Task Missing_expense_returns_false_without_touching_documents()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();

        var deleted = await new DeleteOperatingExpense(store, documents).Handle(999, CancellationToken.None);

        Assert.False(deleted);
        Assert.Empty(documents.DeletedFileNames);
    }

    [Fact]
    public async Task Deleting_an_expense_without_an_attachment_does_not_call_document_storage()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var created = await new CreateOperatingExpense(store, documents).Handle(Fields(), null, CancellationToken.None);

        var deleted = await new DeleteOperatingExpense(store, documents).Handle(created.Record!.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(documents.DeletedFileNames);
    }

    [Fact]
    public async Task Deleting_an_expense_with_an_attachment_removes_the_stored_document()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var attachment = new ExpenseAttachmentInput("invoice.pdf", 3, () => new MemoryStream([1, 2, 3]));
        var created = await new CreateOperatingExpense(store, documents).Handle(Fields(), attachment, CancellationToken.None);
        var storedFileName = created.Record!.AttachmentStoredFileName!;

        var deleted = await new DeleteOperatingExpense(store, documents).Handle(created.Record.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Contains(storedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.ExpenseAttachment, storedFileName));
    }
}
