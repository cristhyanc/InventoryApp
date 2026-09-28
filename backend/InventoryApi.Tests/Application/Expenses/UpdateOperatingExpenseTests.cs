using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class UpdateOperatingExpenseTests
{
    private static OperatingExpenseFields Fields(string description = "Updated insurance") => new(
        new DateTime(2026, 3, 1), ExpenseCategory.Insurance, description, 10m, 1m, 11m,
        null, null, null, null, null, null);

    private static ExpenseAttachmentInput AttachmentInput(string fileName, byte[]? bytes = null) =>
        new(fileName, (bytes ?? [1, 2, 3]).Length, () => new MemoryStream(bytes ?? [1, 2, 3]));

    private static async Task<(FakeOperatingExpenseStore Store, FakeDocumentStorage Documents, int Id, string StoredFileName)>
        SeedWithAttachmentAsync()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var created = await new CreateOperatingExpense(store, documents)
            .Handle(Fields("Original"), AttachmentInput("old.pdf"), CancellationToken.None);
        return (store, documents, created.Record!.Id, created.Record.AttachmentStoredFileName!);
    }

    [Fact]
    public async Task Missing_expense_returns_not_found_without_touching_documents()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UpdateOperatingExpense(store, documents);

        var result = await useCase.Handle(999, Fields(), null, CancellationToken.None);

        Assert.True(result.IsNotFound);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Invalid_fields_return_validation_error_before_any_lookup()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UpdateOperatingExpense(store, documents);

        var result = await useCase.Handle(1, Fields(description: ""), null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.False(result.IsNotFound);
        Assert.Equal(OperatingExpenseDetails.DescriptionRequiredMessage, result.ValidationError);
    }

    [Fact]
    public async Task Update_without_attachment_leaves_the_existing_attachment_untouched()
    {
        var (store, documents, id, storedFileName) = await SeedWithAttachmentAsync();
        var useCase = new UpdateOperatingExpense(store, documents);

        var result = await useCase.Handle(id, Fields("Updated"), null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("Updated", result.Record!.Description);
        Assert.Equal(storedFileName, result.Record.AttachmentStoredFileName);
        Assert.Empty(documents.DeletedFileNames);
    }

    [Fact]
    public async Task Replacing_the_attachment_deletes_the_previous_document_only_after_saving()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        var useCase = new UpdateOperatingExpense(store, documents);

        var result = await useCase.Handle(id, Fields("Updated"), AttachmentInput("new.png"), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("new.png", result.Record!.AttachmentFileName);
        Assert.NotEqual(oldStoredFileName, result.Record.AttachmentStoredFileName);
        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, result.Record.AttachmentStoredFileName!));
        Assert.False(documents.Contains(DocumentCategory.ExpenseAttachment, oldStoredFileName));
        Assert.Contains(oldStoredFileName, documents.DeletedFileNames);
    }

    [Fact]
    public async Task Failed_replacement_preserves_the_existing_attachment()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        var useCase = new UpdateOperatingExpense(store, documents);

        var result = await useCase.Handle(id, Fields("Updated"), AttachmentInput("new.exe"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseAttachmentPolicy.ExtensionErrorMessage, result.ValidationError);
        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, oldStoredFileName));
        var stillCurrent = await store.FindByIdAsync(id, CancellationToken.None);
        Assert.Equal(oldStoredFileName, stillCurrent!.AttachmentStoredFileName);
    }

    [Fact]
    public async Task When_persistence_fails_after_saving_the_new_document_is_cleaned_up_and_old_one_kept()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        store.ThrowOnUpdate = true;
        var useCase = new UpdateOperatingExpense(store, documents);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.Handle(id, Fields("Updated"), AttachmentInput("new.png"), CancellationToken.None));

        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, oldStoredFileName));
        var newStoredFileName = Assert.Single(documents.SavedFileNames, name => name != oldStoredFileName);
        Assert.False(documents.Contains(DocumentCategory.ExpenseAttachment, newStoredFileName));
    }
}
